using Taipower.Safety.Core.Forms.Pr1;
using Xunit;

using Taipower.Safety.Core.Forms.Workflow;
namespace Taipower.Safety.Tests;

public class SafetyWorkflowTests
{
    private static readonly WorkflowActionContext Plain = new();
    private static readonly WorkflowActionContext Jump = new() { IsJump = true };
    private static readonly WorkflowActionContext Proxy = new() { IsProxy = true };

    private static WorkflowTransition Next(WorkflowStep s, WorkflowAction a, WorkflowActionContext? c = null)
    {
        var t = SafetyWorkflow.Next(s, a, c ?? Plain);
        Assert.NotNull(t);
        return t!;
    }

    [Fact]
    public void HappyPath_runs_create_to_closed()
    {
        var step = WorkflowStep.Draft;

        // C1 建單 → 工安組登記桌
        var t = Next(step, WorkflowAction.Submit);
        Assert.Equal(new WorkflowStep(Stage.SafetyReview, Position.Assign, Transit.Submitted), t.To);
        Assert.Equal(AssigneeRule.SafetyDesk, t.Assignee);
        step = t.To;

        // C3 登記桌派課長（仍在 Assign 關）→ 課長派經辦（進 Officer 關）
        var assignChief = Next(step, WorkflowAction.AssignChief);
        Assert.Equal(Position.Assign, assignChief.To.Position);
        Assert.Equal(AssigneeRule.SpecificPerson, assignChief.Assignee);
        var assignOfficer = Next(assignChief.To with { Transit = Transit.Working }, WorkflowAction.AssignOfficer);
        Assert.Equal(Position.Officer, assignOfficer.To.Position);
        step = new WorkflowStep(Stage.SafetyReview, Position.Officer, Transit.Working);

        // 經辦上呈課長 → 經理 → 下一階段登記桌
        step = Next(step, WorkflowAction.Submit).To;
        Assert.Equal(Position.Chief, step.Position);
        step = Next(step with { Transit = Transit.Working }, WorkflowAction.Submit).To;
        Assert.Equal(Position.Manager, step.Position);
        t = Next(step with { Transit = Transit.Working }, WorkflowAction.Submit);
        Assert.Equal(new WorkflowStep(Stage.Improvement, Position.Assign, Transit.Submitted), t.To);
        Assert.Equal(AssigneeRule.ImprovementDesk, t.Assignee);

        // C4 經辦組 → C5 工安組查證
        var afterC4 = RunReviewToNextStage(Stage.Improvement);
        Assert.Equal(Stage.Verification, afterC4.Stage);

        // C5 → C6 巡查領隊
        t = Next(new WorkflowStep(Stage.Verification, Position.Manager, Transit.Working), WorkflowAction.Submit);
        Assert.Equal(new WorkflowStep(Stage.Approval, Position.Leader, Transit.Submitted), t.To);
        Assert.Equal(AssigneeRule.Leader, t.Assignee);

        // C6 領隊同意結案 → 副處長 → 副處長 → 處長核可結案
        t = Next(new WorkflowStep(Stage.Approval, Position.Leader, Transit.Working), WorkflowAction.CloseDecision, new WorkflowActionContext { AgreeClose = true });
        Assert.Equal(AssigneeRule.NextApprover, t.Assignee);

        t = Next(new WorkflowStep(Stage.Approval, Position.Director, Transit.Working), WorkflowAction.Approve);
        Assert.Equal(FormStatus.Closed, t.Status);
        Assert.False(t.SendEmail);
    }

    private static WorkflowStep RunReviewToNextStage(Stage stage)
    {
        var step = new WorkflowStep(stage, Position.Officer, Transit.Working);
        step = Next(step, WorkflowAction.Submit).To;                        // → chief
        step = Next(step with { Transit = Transit.Working }, WorkflowAction.Submit).To;  // → manager
        return Next(step with { Transit = Transit.Working }, WorkflowAction.Submit).To;  // → next stage
    }

    [Fact]
    public void Jump_skips_chief_and_goes_to_manager()
    {
        var officer = new WorkflowStep(Stage.SafetyReview, Position.Officer, Transit.Working);
        var t = Next(officer, WorkflowAction.Submit, Jump);
        Assert.Equal(Position.Manager, t.To.Position);
        Assert.Equal(AssigneeRule.Manager, t.Assignee);
    }

    [Fact]
    public void Proxy_chief_forwards_to_proxy_manager()
    {
        var chief = new WorkflowStep(Stage.SafetyReview, Position.Chief, Transit.Working);
        Assert.Equal(AssigneeRule.ProxyManager, Next(chief, WorkflowAction.Submit, Proxy).Assignee);
        Assert.Equal(AssigneeRule.Manager, Next(chief, WorkflowAction.Submit, Plain).Assignee);
    }

    [Theory]
    [InlineData(Stage.SafetyReview)]
    [InlineData(Stage.Improvement)]
    [InlineData(Stage.Verification)]
    public void Manager_can_return_to_officer_and_chief(Stage stage)
    {
        var mgr = new WorkflowStep(stage, Position.Manager, Transit.Working);
        Assert.Equal(AssigneeRule.OfficerOfRecord, Next(mgr, WorkflowAction.ReturnToOfficer).Assignee);
        Assert.Equal(AssigneeRule.ChiefOfRecord, Next(mgr, WorkflowAction.ReturnToChief).Assignee);
    }

    [Fact]
    public void Desk_returns_to_creator_and_bumps_revision()
    {
        var desk = new WorkflowStep(Stage.SafetyReview, Position.Assign, Transit.Working);
        var t = Next(desk, WorkflowAction.ReturnToCreator);
        Assert.Equal(Stage.Create, t.To.Stage);
        Assert.Equal(AssigneeRule.Creator, t.Assignee);
        Assert.True(t.IncrementRevision);
    }

    [Fact]
    public void Leader_disagree_sends_back_to_improvement_and_bumps_revision()
    {
        var leader = new WorkflowStep(Stage.Approval, Position.Leader, Transit.Working);
        var t = Next(leader, WorkflowAction.CloseDecision, new WorkflowActionContext { AgreeClose = false });
        Assert.Equal(new WorkflowStep(Stage.Improvement, Position.Assign, Transit.Submitted), t.To);
        Assert.True(t.IncrementRevision);
    }

    [Fact]
    public void Director_reject_voids_the_form()
    {
        var dir = new WorkflowStep(Stage.Approval, Position.Director, Transit.Working);
        var t = Next(dir, WorkflowAction.Reject);
        Assert.Equal(FormStatus.Voided, t.Status);
        Assert.Equal(AssigneeRule.Creator, t.Assignee);
    }

    [Fact]
    public void Director_return_goes_back_to_leader()
    {
        var dir = new WorkflowStep(Stage.Approval, Position.Director, Transit.Working);
        var t = Next(dir, WorkflowAction.ReturnToLeader);
        Assert.Equal(new WorkflowStep(Stage.Approval, Position.Leader, Transit.Submitted), t.To);
    }

    [Fact]
    public void Disallowed_actions_return_null()
    {
        // 建單階段不能核可
        Assert.Null(SafetyWorkflow.Next(WorkflowStep.Draft, WorkflowAction.Approve, Plain));
        // 經辦不能退回課長
        Assert.Null(SafetyWorkflow.Next(new WorkflowStep(Stage.SafetyReview, Position.Officer, Transit.Working), WorkflowAction.ReturnToChief, Plain));
        // 處長不能抽回
        Assert.DoesNotContain(WorkflowAction.Withdraw, SafetyWorkflow.AllowedActions(new WorkflowStep(Stage.Approval, Position.Director, Transit.Working)));
    }

    [Fact]
    public void DisplayNumber_substitutes_revision_into_last_char()
    {
        var form = new Pr1Form { FormNumber = "SI-1091110-01-0", Revision = 0 };
        Assert.Equal("SI-1091110-01-0", form.DisplayNumber);
        form.Revision = 2;
        Assert.Equal("SI-1091110-01-2", form.DisplayNumber);
    }
}

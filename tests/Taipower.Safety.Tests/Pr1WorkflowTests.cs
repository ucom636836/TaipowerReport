using Taipower.Safety.Core.Forms.Pr1;
using Xunit;

namespace Taipower.Safety.Tests;

public class Pr1WorkflowTests
{
    private static readonly Pr1ActionContext Plain = new();
    private static readonly Pr1ActionContext Jump = new() { IsJump = true };
    private static readonly Pr1ActionContext Proxy = new() { IsProxy = true };

    private static Pr1Transition Next(Pr1Step s, Pr1Action a, Pr1ActionContext? c = null)
    {
        var t = Pr1Workflow.Next(s, a, c ?? Plain);
        Assert.NotNull(t);
        return t!;
    }

    [Fact]
    public void HappyPath_runs_create_to_closed()
    {
        var step = Pr1Step.Draft;

        // C1 建單 → 工安組登記桌
        var t = Next(step, Pr1Action.Submit);
        Assert.Equal(new Pr1Step(Stage.SafetyReview, Position.Assign, Transit.Submitted), t.To);
        Assert.Equal(AssigneeRule.SafetyDesk, t.Assignee);
        step = t.To;

        // C3 登記桌派課長（仍在 Assign 關）→ 課長派經辦（進 Officer 關）
        var assignChief = Next(step, Pr1Action.AssignChief);
        Assert.Equal(Position.Assign, assignChief.To.Position);
        Assert.Equal(AssigneeRule.SpecificPerson, assignChief.Assignee);
        var assignOfficer = Next(assignChief.To with { Transit = Transit.Working }, Pr1Action.AssignOfficer);
        Assert.Equal(Position.Officer, assignOfficer.To.Position);
        step = new Pr1Step(Stage.SafetyReview, Position.Officer, Transit.Working);

        // 經辦上呈課長 → 經理 → 下一階段登記桌
        step = Next(step, Pr1Action.Submit).To;
        Assert.Equal(Position.Chief, step.Position);
        step = Next(step with { Transit = Transit.Working }, Pr1Action.Submit).To;
        Assert.Equal(Position.Manager, step.Position);
        t = Next(step with { Transit = Transit.Working }, Pr1Action.Submit);
        Assert.Equal(new Pr1Step(Stage.Improvement, Position.Assign, Transit.Submitted), t.To);
        Assert.Equal(AssigneeRule.ImprovementDesk, t.Assignee);

        // C4 經辦組 → C5 工安組查證
        var afterC4 = RunReviewToNextStage(Stage.Improvement);
        Assert.Equal(Stage.Verification, afterC4.Stage);

        // C5 → C6 巡查領隊
        t = Next(new Pr1Step(Stage.Verification, Position.Manager, Transit.Working), Pr1Action.Submit);
        Assert.Equal(new Pr1Step(Stage.Approval, Position.Leader, Transit.Submitted), t.To);
        Assert.Equal(AssigneeRule.Leader, t.Assignee);

        // C6 領隊同意結案 → 副處長 → 副處長 → 處長核可結案
        t = Next(new Pr1Step(Stage.Approval, Position.Leader, Transit.Working), Pr1Action.CloseDecision, new Pr1ActionContext { AgreeClose = true });
        Assert.Equal(AssigneeRule.NextApprover, t.Assignee);

        t = Next(new Pr1Step(Stage.Approval, Position.Director, Transit.Working), Pr1Action.Approve);
        Assert.Equal(FormStatus.Closed, t.Status);
        Assert.False(t.SendEmail);
    }

    private static Pr1Step RunReviewToNextStage(Stage stage)
    {
        var step = new Pr1Step(stage, Position.Officer, Transit.Working);
        step = Next(step, Pr1Action.Submit).To;                        // → chief
        step = Next(step with { Transit = Transit.Working }, Pr1Action.Submit).To;  // → manager
        return Next(step with { Transit = Transit.Working }, Pr1Action.Submit).To;  // → next stage
    }

    [Fact]
    public void Jump_skips_chief_and_goes_to_manager()
    {
        var officer = new Pr1Step(Stage.SafetyReview, Position.Officer, Transit.Working);
        var t = Next(officer, Pr1Action.Submit, Jump);
        Assert.Equal(Position.Manager, t.To.Position);
        Assert.Equal(AssigneeRule.Manager, t.Assignee);
    }

    [Fact]
    public void Proxy_chief_forwards_to_proxy_manager()
    {
        var chief = new Pr1Step(Stage.SafetyReview, Position.Chief, Transit.Working);
        Assert.Equal(AssigneeRule.ProxyManager, Next(chief, Pr1Action.Submit, Proxy).Assignee);
        Assert.Equal(AssigneeRule.Manager, Next(chief, Pr1Action.Submit, Plain).Assignee);
    }

    [Theory]
    [InlineData(Stage.SafetyReview)]
    [InlineData(Stage.Improvement)]
    [InlineData(Stage.Verification)]
    public void Manager_can_return_to_officer_and_chief(Stage stage)
    {
        var mgr = new Pr1Step(stage, Position.Manager, Transit.Working);
        Assert.Equal(AssigneeRule.OfficerOfRecord, Next(mgr, Pr1Action.ReturnToOfficer).Assignee);
        Assert.Equal(AssigneeRule.ChiefOfRecord, Next(mgr, Pr1Action.ReturnToChief).Assignee);
    }

    [Fact]
    public void Desk_returns_to_creator_and_bumps_revision()
    {
        var desk = new Pr1Step(Stage.SafetyReview, Position.Assign, Transit.Working);
        var t = Next(desk, Pr1Action.ReturnToCreator);
        Assert.Equal(Stage.Create, t.To.Stage);
        Assert.Equal(AssigneeRule.Creator, t.Assignee);
        Assert.True(t.IncrementRevision);
    }

    [Fact]
    public void Leader_disagree_sends_back_to_improvement_and_bumps_revision()
    {
        var leader = new Pr1Step(Stage.Approval, Position.Leader, Transit.Working);
        var t = Next(leader, Pr1Action.CloseDecision, new Pr1ActionContext { AgreeClose = false });
        Assert.Equal(new Pr1Step(Stage.Improvement, Position.Assign, Transit.Submitted), t.To);
        Assert.True(t.IncrementRevision);
    }

    [Fact]
    public void Director_reject_voids_the_form()
    {
        var dir = new Pr1Step(Stage.Approval, Position.Director, Transit.Working);
        var t = Next(dir, Pr1Action.Reject);
        Assert.Equal(FormStatus.Voided, t.Status);
        Assert.Equal(AssigneeRule.Creator, t.Assignee);
    }

    [Fact]
    public void Director_return_goes_back_to_leader()
    {
        var dir = new Pr1Step(Stage.Approval, Position.Director, Transit.Working);
        var t = Next(dir, Pr1Action.ReturnToLeader);
        Assert.Equal(new Pr1Step(Stage.Approval, Position.Leader, Transit.Submitted), t.To);
    }

    [Fact]
    public void Disallowed_actions_return_null()
    {
        // 建單階段不能核可
        Assert.Null(Pr1Workflow.Next(Pr1Step.Draft, Pr1Action.Approve, Plain));
        // 經辦不能退回課長
        Assert.Null(Pr1Workflow.Next(new Pr1Step(Stage.SafetyReview, Position.Officer, Transit.Working), Pr1Action.ReturnToChief, Plain));
        // 處長不能抽回
        Assert.DoesNotContain(Pr1Action.Withdraw, Pr1Workflow.AllowedActions(new Pr1Step(Stage.Approval, Position.Director, Transit.Working)));
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

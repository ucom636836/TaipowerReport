using Taipower.Safety.Core.Forms.Pr3;
using Taipower.Safety.Core.Forms.Workflow;
using Xunit;

namespace Taipower.Safety.Tests;

/// <summary>Pr3 專屬狀態機測試：起單→經辦組(S2)→工安組(S3)→結案/持續追蹤迴圈。</summary>
public class Pr3WorkflowTests
{
    private static Pr3Step Step(Pr3Stage s, Position p, Transit t = Transit.Working) => new(s, p, t);
    private static Pr3ActionContext Ctx(bool filerIsInspector = true, bool jump = false) =>
        new() { FilerIsInspector = filerIsInspector, IsJump = jump };

    [Fact]
    public void Filing_creator_submit_self_inspector_goes_to_handling_desk()
    {
        var t = Pr3Workflow.Next(Pr3Step.Draft, Pr3Action.Submit, Ctx(filerIsInspector: true))!;
        Assert.Equal(Step(Pr3Stage.HandlingReview, Position.Assign, Transit.Submitted), t.To);
        Assert.Equal(Pr3AssigneeRule.HandlingDesk, t.Assignee);
    }

    [Fact]
    public void Filing_creator_submit_other_filer_goes_to_inspector_first()
    {
        var t = Pr3Workflow.Next(Pr3Step.Draft, Pr3Action.Submit, Ctx(filerIsInspector: false))!;
        Assert.Equal(Step(Pr3Stage.Filing, Position.Inspector, Transit.Submitted), t.To);
        Assert.Equal(Pr3AssigneeRule.Inspector, t.Assignee);
    }

    [Fact]
    public void Inspector_confirm_goes_to_handling_desk()
    {
        var t = Pr3Workflow.Next(Step(Pr3Stage.Filing, Position.Inspector), Pr3Action.Submit, Ctx())!;
        Assert.Equal(Step(Pr3Stage.HandlingReview, Position.Assign, Transit.Submitted), t.To);
        Assert.Equal(Pr3AssigneeRule.HandlingDesk, t.Assignee);
    }

    [Fact]
    public void Inspector_return_to_filer_increments_retimes()
    {
        var t = Pr3Workflow.Next(Step(Pr3Stage.Filing, Position.Inspector), Pr3Action.ReturnToFiler, Ctx())!;
        Assert.Equal(Pr3Stage.Filing, t.To.Stage);
        Assert.Equal(Position.Creator, t.To.Position);
        Assert.Equal(Pr3AssigneeRule.Filer, t.Assignee);
        Assert.True(t.IncrementRetimes);
    }

    [Fact]
    public void Handling_officer_submit_jump_goes_to_manager()
    {
        var t = Pr3Workflow.Next(Step(Pr3Stage.HandlingReview, Position.Officer), Pr3Action.Submit, Ctx(jump: true))!;
        Assert.Equal(Position.Manager, t.To.Position);
        Assert.Equal(Pr3AssigneeRule.Manager, t.Assignee);
    }

    [Fact]
    public void Handling_officer_submit_no_jump_goes_to_chief()
    {
        var t = Pr3Workflow.Next(Step(Pr3Stage.HandlingReview, Position.Officer), Pr3Action.Submit, Ctx(jump: false))!;
        Assert.Equal(Position.Chief, t.To.Position);
        Assert.Equal(Pr3AssigneeRule.AssignedChief, t.Assignee);
    }

    [Fact]
    public void Handling_manager_submit_advances_to_safety_desk()
    {
        var t = Pr3Workflow.Next(Step(Pr3Stage.HandlingReview, Position.Manager), Pr3Action.Submit, Ctx())!;
        Assert.Equal(Step(Pr3Stage.SafetyReview, Position.Assign, Transit.Submitted), t.To);
        Assert.Equal(Pr3AssigneeRule.SafetyDesk, t.Assignee);
    }

    [Fact]
    public void Safety_desk_return_to_handling_loops_back_and_increments_retimes()
    {
        var t = Pr3Workflow.Next(Step(Pr3Stage.SafetyReview, Position.Assign), Pr3Action.ReturnToHandling, Ctx())!;
        Assert.Equal(Step(Pr3Stage.HandlingReview, Position.Assign, Transit.Submitted), t.To);
        Assert.Equal(Pr3AssigneeRule.HandlingDesk, t.Assignee);
        Assert.True(t.IncrementRetimes);
    }

    [Fact]
    public void Safety_manager_continue_tracking_loops_to_handling_and_increments_archive()
    {
        var t = Pr3Workflow.Next(Step(Pr3Stage.SafetyReview, Position.Manager), Pr3Action.ContinueTracking, Ctx())!;
        Assert.Equal(Step(Pr3Stage.HandlingReview, Position.Assign, Transit.Submitted), t.To);
        Assert.Equal(Pr3AssigneeRule.HandlingDesk, t.Assignee);
        Assert.True(t.IncrementArchive);
        Assert.Equal(FormStatus.InProgress, t.Status);
    }

    [Fact]
    public void Safety_manager_close_closes_form()
    {
        var t = Pr3Workflow.Next(Step(Pr3Stage.SafetyReview, Position.Manager), Pr3Action.Close, Ctx())!;
        Assert.Equal(FormStatus.Closed, t.Status);
        Assert.False(t.SendEmail);
    }

    [Fact]
    public void Disallowed_action_returns_null()
    {
        Assert.Null(Pr3Workflow.Next(Pr3Step.Draft, Pr3Action.Close, Ctx()));
        Assert.Null(Pr3Workflow.Next(Step(Pr3Stage.HandlingReview, Position.Officer), Pr3Action.ContinueTracking, Ctx()));
    }
}

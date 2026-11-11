using Taipower.Safety.Core.Forms.Pr4;
using Taipower.Safety.Core.Forms.Workflow;
using Xunit;

namespace Taipower.Safety.Tests;

/// <summary>Pr4 專屬狀態機測試：檢驗員簽核 → (需改善?) 經辦組查證 → 結案。</summary>
public class Pr4WorkflowTests
{
    private static Pr4Step Step(Pr4Stage s, Position p, Transit t = Transit.Working) => new(s, p, t);
    private static Pr4ActionContext Ctx(bool need = true, bool jump = false) => new() { NeedImprove = need, IsJump = jump };

    [Fact]
    public void Inspection_creator_submit_goes_to_group_chief()
    {
        var t = Pr4Workflow.Next(Pr4Step.Draft, Pr4Action.Submit, Ctx())!;
        Assert.Equal(Step(Pr4Stage.Inspection, Position.Chief, Transit.Submitted), t.To);
        Assert.Equal(Pr4AssigneeRule.GroupChief, t.Assignee);
    }

    [Fact]
    public void Inspection_chief_submit_goes_to_manager()
    {
        var t = Pr4Workflow.Next(Step(Pr4Stage.Inspection, Position.Chief), Pr4Action.Submit, Ctx())!;
        Assert.Equal(Step(Pr4Stage.Inspection, Position.Manager, Transit.Submitted), t.To);
        Assert.Equal(Pr4AssigneeRule.GroupManager, t.Assignee);
    }

    [Fact]
    public void Inspection_chief_return_to_creator_increments_retimes()
    {
        var t = Pr4Workflow.Next(Step(Pr4Stage.Inspection, Position.Chief), Pr4Action.ReturnToCreator, Ctx())!;
        Assert.Equal(Position.Creator, t.To.Position);
        Assert.Equal(Pr4AssigneeRule.Creator, t.Assignee);
        Assert.True(t.IncrementRetimes);
    }

    [Fact]
    public void Inspection_manager_submit_need_improve_goes_to_handling_desk()
    {
        var t = Pr4Workflow.Next(Step(Pr4Stage.Inspection, Position.Manager), Pr4Action.Submit, Ctx(need: true))!;
        Assert.Equal(Step(Pr4Stage.Handling, Position.Assign, Transit.Submitted), t.To);
        Assert.Equal(Pr4AssigneeRule.HandlingDesk, t.Assignee);
        Assert.Equal(FormStatus.InProgress, t.Status);
    }

    [Fact]
    public void Inspection_manager_submit_no_improve_closes()
    {
        var t = Pr4Workflow.Next(Step(Pr4Stage.Inspection, Position.Manager), Pr4Action.Submit, Ctx(need: false))!;
        Assert.Equal(Pr4Stage.Closed, t.To.Stage);
        Assert.Equal(FormStatus.Closed, t.Status);
        Assert.False(t.SendEmail);
    }

    [Fact]
    public void Handling_officer_submit_no_jump_goes_to_chief()
    {
        var t = Pr4Workflow.Next(Step(Pr4Stage.Handling, Position.Officer), Pr4Action.Submit, Ctx(jump: false))!;
        Assert.Equal(Position.Chief, t.To.Position);
        Assert.Equal(Pr4AssigneeRule.AssignedChief, t.Assignee);
    }

    [Fact]
    public void Handling_officer_submit_jump_goes_to_manager()
    {
        var t = Pr4Workflow.Next(Step(Pr4Stage.Handling, Position.Officer), Pr4Action.Submit, Ctx(jump: true))!;
        Assert.Equal(Position.Manager, t.To.Position);
        Assert.Equal(Pr4AssigneeRule.GroupManager, t.Assignee);
    }

    [Fact]
    public void Handling_manager_submit_closes()
    {
        var t = Pr4Workflow.Next(Step(Pr4Stage.Handling, Position.Manager), Pr4Action.Submit, Ctx())!;
        Assert.Equal(Pr4Stage.Closed, t.To.Stage);
        Assert.Equal(FormStatus.Closed, t.Status);
    }

    [Fact]
    public void Handling_assign_assign_officer_goes_to_officer()
    {
        var t = Pr4Workflow.Next(Step(Pr4Stage.Handling, Position.Assign, Transit.Submitted), Pr4Action.AssignOfficer, Ctx())!;
        Assert.Equal(Step(Pr4Stage.Handling, Position.Officer, Transit.Submitted), t.To);
        Assert.Equal(Pr4AssigneeRule.SpecificPerson, t.Assignee);
    }

    [Fact]
    public void Disallowed_action_returns_null()
    {
        Assert.Null(Pr4Workflow.Next(Pr4Step.Draft, Pr4Action.AssignOfficer, Ctx()));
        Assert.Null(Pr4Workflow.Next(Step(Pr4Stage.Inspection, Position.Creator), Pr4Action.ReturnToCreator, Ctx()));
    }
}

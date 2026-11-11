using Taipower.Safety.Core.Common;
using Taipower.Safety.Core.Forms.Workflow;
using Taipower.Safety.Core.Org;

namespace Taipower.Safety.Core.Forms.Pr4;

public sealed record Pr4CreateRequest(
    DateTime SpotCheckDate, int ProjectId, int ContractorId, string? Location, string? ArriveTime, string? LeaveTime,
    string GroupId, DateTime? ImproveDeadline, bool NeedImprove, IReadOnlyList<Pr4Item> Items);

public sealed record Pr4ActionRequest(
    long FormId, Pr4Action Action, string? Comment,
    string? SpecificPersonId = null, bool IsJump = false);

public sealed record Pr4ActionResult(bool Ok, string? Error = null, long FormId = 0);

/// <summary>
/// Pr4（承攬商工作場所工安抽查紀錄表）建單與簽核的應用服務。使用 Pr4 專屬
/// <see cref="Pr4Workflow"/> 狀態機與 <see cref="IPr4AssigneeResolver"/>，共用底盤與組織導航。
/// </summary>
public sealed class Pr4Service(
    IPr4Repository repo,
    IPr4AssigneeResolver resolver,
    IEmailSender email,
    Pr4EmailComposer composer)
{
    public async Task<Pr4ActionResult> CreateAsync(Pr4CreateRequest req, CurrentUser actor, CancellationToken ct = default)
    {
        if (req.ProjectId <= 0 || req.ContractorId <= 0 || string.IsNullOrEmpty(req.GroupId))
            return new Pr4ActionResult(false, "請完整填寫工程、承攬商與經辦組別。");
        if (req.Items.Count == 0)
            return new Pr4ActionResult(false, "請至少填寫一項優點、缺失或建議改善事項。");

        var form = new Pr4Form
        {
            SpotCheckDate = req.SpotCheckDate,
            ProjectId = req.ProjectId,
            ContractorId = req.ContractorId,
            Location = req.Location,
            ArriveTime = req.ArriveTime,
            LeaveTime = req.LeaveTime,
            GroupId = req.GroupId,
            ImproveDeadline = req.ImproveDeadline,
            NeedImprove = req.NeedImprove,
            CreatedBy = actor.EmployeeId,
            Items = req.Items.ToList(),
        };

        var ctx = new Pr4ActionContext { NeedImprove = req.NeedImprove };
        var transition = Pr4Workflow.Next(Pr4Step.Draft, Pr4Action.Submit, ctx)!;
        var recipient = await resolver.ResolveAsync(form, transition.Assignee, actor.EmployeeId, null, ct);
        if (recipient is null)
            return new Pr4ActionResult(false, "找不到經辦組別的課長，請確認組織與人員設定。");

        form.Step = transition.To;
        form.CurrentHolderId = actor.EmployeeId;
        form.PendingRecipientId = recipient.EmployeeId;

        for (var attempt = 0; ; attempt++)
        {
            form.FormNumber = await repo.NextFormNumberAsync(req.SpotCheckDate, ct);
            try
            {
                var id = await repo.CreateAsync(form, actor.Name, ct);
                await NotifyAsync(form, recipient, transition.HistoryLabel ?? "建單", ct);
                return new Pr4ActionResult(true, FormId: id);
            }
            catch (Exception) when (attempt < 3)
            {
                // 單號 UNIQUE 撞號，重算後重試。
            }
        }
    }

    public async Task<Pr4ActionResult> ActAsync(Pr4ActionRequest req, CurrentUser actor, CancellationToken ct = default)
    {
        var form = await repo.GetAsync(req.FormId, ct);
        if (form is null) return new Pr4ActionResult(false, "找不到此單。");
        if (form.Status != FormStatus.InProgress) return new Pr4ActionResult(false, "此單已結案，無法再操作。");
        if (actor.EmployeeId != form.PendingRecipientId && actor.EmployeeId != form.CurrentHolderId)
            return new Pr4ActionResult(false, "您目前不是此單的處理人。");

        var ctx = new Pr4ActionContext { NeedImprove = form.NeedImprove, IsJump = req.IsJump };
        var transition = Pr4Workflow.Next(form.Step, req.Action, ctx);
        if (transition is null) return new Pr4ActionResult(false, "目前狀態不允許這個動作。");

        ApplySideData(form, req);

        var assignee = await resolver.ResolveAsync(form, transition.Assignee, actor.EmployeeId, req.SpecificPersonId, ct);
        if (assignee is null && transition.Assignee != Pr4AssigneeRule.None)
            return new Pr4ActionResult(false, "找不到下一關的處理人，請確認組織與人員設定。");

        form.CurrentHolderId = actor.EmployeeId;
        form.PendingRecipientId = assignee?.EmployeeId;

        try
        {
            await repo.ApplyTransitionAsync(form, transition, actor.EmployeeId, actor.Name, req.Comment, ct);
        }
        catch (Exception ex) when (ex.GetType().Name == "Pr4ConcurrencyException")
        {
            return new Pr4ActionResult(false, ex.Message);
        }

        if (transition.SendEmail && assignee is not null)
            await NotifyAsync(form, assignee, transition.HistoryLabel ?? "簽核", ct);

        return new Pr4ActionResult(true, FormId: form.FormId);
    }

    public async Task OpenAsync(long formId, CurrentUser actor, CancellationToken ct = default)
    {
        var form = await repo.GetAsync(formId, ct);
        if (form is null || form.PendingRecipientId != actor.EmployeeId || form.CurrentHolderId == actor.EmployeeId) return;
        var open = form.Step with { Transit = Transit.Working };
        form.CurrentHolderId = actor.EmployeeId;
        await repo.ApplyTransitionAsync(form, new Pr4Transition(open, Pr4AssigneeRule.Self, false, HistoryLabel: "承接"),
            actor.EmployeeId, actor.Name, null, ct);
    }

    private static void ApplySideData(Pr4Form form, Pr4ActionRequest req)
    {
        var stage = form.Step.Stage;
        var pos = form.Step.Position;

        if (req.Action is Pr4Action.AssignChief)
            form.HandlingChiefId = req.SpecificPersonId;
        else if (req.Action is Pr4Action.AssignOfficer)
            form.HandlingOfficerId = req.SpecificPersonId;

        if (req.Action is Pr4Action.Submit)
        {
            var holder = form.CurrentHolderId;
            if (stage == Pr4Stage.Inspection)
            {
                if (pos == Position.Chief) form.InspectionChiefId = holder;
                else if (pos == Position.Manager) form.InspectionManagerId = holder;
            }
            else if (stage == Pr4Stage.Handling)
            {
                if (pos == Position.Officer) { form.HandlingOfficerId = holder; form.IsJump = req.IsJump; }
                else if (pos == Position.Chief) form.HandlingChiefId = holder;
                else if (pos == Position.Manager) form.HandlingManagerId = holder;
            }
        }
    }

    private async Task NotifyAsync(Pr4Form form, Employee recipient, string actionLabel, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(recipient.Email)) return;
        var msg = await composer.ComposeAsync(form, recipient, actionLabel, ct);
        await email.SendAsync(msg, ct);
    }
}

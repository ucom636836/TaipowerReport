using Taipower.Safety.Core.Common;
using Taipower.Safety.Core.Forms.Workflow;
using Taipower.Safety.Core.Org;

namespace Taipower.Safety.Core.Forms.Pr3;

public sealed record Pr3CreateRequest(
    string InspectorId, DateTime InspectDate, string? InspectTime, string? Attendants, string? Location, string? Target,
    int ProjectId, int ContractorId, string GroupId, DateTime? ImproveDeadline, IReadOnlyList<Pr3Item> Items);

public sealed record Pr3ActionRequest(
    long FormId, Pr3Action Action, string? Comment,
    string? SpecificPersonId = null, bool IsJump = false);

public sealed record Pr3ActionResult(bool Ok, string? Error = null, long FormId = 0);

/// <summary>
/// Pr3（走動管理紀錄暨追蹤表）建單與簽核的應用服務。用 Pr3 專屬的
/// <see cref="Pr3Workflow"/> 狀態機與 <see cref="IPr3AssigneeResolver"/>（動態組別路由），
/// 但共用底盤（FormInstance/WorkflowHistory）與組織導航。
/// </summary>
public sealed class Pr3Service(
    IPr3Repository repo,
    IPr3AssigneeResolver resolver,
    IEmailSender email,
    Pr3EmailComposer composer)
{
    public async Task<Pr3ActionResult> CreateAsync(Pr3CreateRequest req, CurrentUser actor, CancellationToken ct = default)
    {
        if (req.ProjectId <= 0 || req.ContractorId <= 0 || string.IsNullOrEmpty(req.InspectorId) || string.IsNullOrEmpty(req.GroupId))
            return new Pr3ActionResult(false, "請完整填寫工程、承攬商、巡查人與辦理組別。");
        if (req.Items.Count == 0)
            return new Pr3ActionResult(false, "請至少填寫一項優點、缺點或建議改善事項。");

        var form = new Pr3Form
        {
            InspectorId = req.InspectorId,
            InspectDate = req.InspectDate,
            InspectTime = req.InspectTime,
            Attendants = req.Attendants,
            Location = req.Location,
            Target = req.Target,
            ProjectId = req.ProjectId,
            ContractorId = req.ContractorId,
            GroupId = req.GroupId,
            ImproveDeadline = req.ImproveDeadline,
            CreatedBy = actor.EmployeeId,
            Items = req.Items.ToList(),
        };

        var ctx = new Pr3ActionContext { FilerIsInspector = req.InspectorId == actor.EmployeeId };
        var transition = Pr3Workflow.Next(Pr3Step.Draft, Pr3Action.Submit, ctx)!;
        var recipient = await resolver.ResolveAsync(form, transition.Assignee, actor.EmployeeId, null, ct);
        if (recipient is null)
            return new Pr3ActionResult(false, ctx.FilerIsInspector ? "找不到辦理組別的登記桌，請先於組織維護設定。" : "找不到巡查人。");

        form.Step = transition.To;
        form.CurrentHolderId = actor.EmployeeId;
        form.PendingRecipientId = recipient.EmployeeId;

        for (var attempt = 0; ; attempt++)
        {
            form.FormNumber = await repo.NextFormNumberAsync(req.InspectDate, ct);
            try
            {
                var id = await repo.CreateAsync(form, actor.Name, ct);
                await NotifyAsync(form, recipient, transition.HistoryLabel ?? "建單", ct);
                return new Pr3ActionResult(true, FormId: id);
            }
            catch (Exception) when (attempt < 3)
            {
                // 單號 UNIQUE 撞號，重算後重試。
            }
        }
    }

    public async Task<Pr3ActionResult> ActAsync(Pr3ActionRequest req, CurrentUser actor, CancellationToken ct = default)
    {
        var form = await repo.GetAsync(req.FormId, ct);
        if (form is null) return new Pr3ActionResult(false, "找不到此單。");
        if (form.Status != FormStatus.InProgress) return new Pr3ActionResult(false, "此單已結案，無法再操作。");
        if (actor.EmployeeId != form.PendingRecipientId && actor.EmployeeId != form.CurrentHolderId)
            return new Pr3ActionResult(false, "您目前不是此單的處理人。");

        var ctx = new Pr3ActionContext { IsJump = req.IsJump, FilerIsInspector = form.InspectorId == form.CreatedBy };
        var transition = Pr3Workflow.Next(form.Step, req.Action, ctx);
        if (transition is null) return new Pr3ActionResult(false, "目前狀態不允許這個動作。");

        ApplySideData(form, req, ctx);

        var assignee = await resolver.ResolveAsync(form, transition.Assignee, actor.EmployeeId, req.SpecificPersonId, ct);
        if (assignee is null && transition.Assignee != Pr3AssigneeRule.None)
            return new Pr3ActionResult(false, "找不到下一關的處理人，請確認組織與人員設定。");

        form.CurrentHolderId = actor.EmployeeId;
        form.PendingRecipientId = assignee?.EmployeeId;

        try
        {
            await repo.ApplyTransitionAsync(form, transition, actor.EmployeeId, actor.Name, req.Comment, ct);
        }
        catch (Exception ex) when (ex.GetType().Name == "Pr3ConcurrencyException")
        {
            return new Pr3ActionResult(false, ex.Message);
        }

        if (transition.SendEmail && assignee is not null)
            await NotifyAsync(form, assignee, transition.HistoryLabel ?? "簽核", ct);

        return new Pr3ActionResult(true, FormId: form.FormId);
    }

    /// <summary>收件人開啟待辦，承接為目前持有人（不改變步驟）。</summary>
    public async Task OpenAsync(long formId, CurrentUser actor, CancellationToken ct = default)
    {
        var form = await repo.GetAsync(formId, ct);
        if (form is null || form.PendingRecipientId != actor.EmployeeId || form.CurrentHolderId == actor.EmployeeId) return;
        var open = form.Step with { Transit = Transit.Working };
        form.CurrentHolderId = actor.EmployeeId;
        await repo.ApplyTransitionAsync(form, new Pr3Transition(open, Pr3AssigneeRule.Self, false, HistoryLabel: "承接"),
            actor.EmployeeId, actor.Name, null, ct);
    }

    private static void ApplySideData(Pr3Form form, Pr3ActionRequest req, Pr3ActionContext ctx)
    {
        var stage = form.Step.Stage;

        if (req.Action is Pr3Action.AssignChief)
            SetChief(form, stage, req.SpecificPersonId);
        else if (req.Action is Pr3Action.AssignOfficer)
            SetOfficer(form, stage, req.SpecificPersonId);

        if (req.Action is Pr3Action.Submit && form.Step.Position == Position.Officer)
        {
            SetOfficer(form, stage, form.CurrentHolderId);
            form.IsJump = ctx.IsJump;
        }
        if (req.Action is Pr3Action.Submit && form.Step.Position == Position.Chief)
            SetChief(form, stage, form.CurrentHolderId);
        if (req.Action is Pr3Action.Submit && form.Step.Position == Position.Manager)
            SetManager(form, stage, form.CurrentHolderId);
    }

    private static void SetOfficer(Pr3Form f, Pr3Stage s, string? id)
    {
        if (s == Pr3Stage.HandlingReview) f.HandlingOfficerId = id;
        else if (s == Pr3Stage.SafetyReview) f.SafetyOfficerId = id;
    }

    private static void SetChief(Pr3Form f, Pr3Stage s, string? id)
    {
        if (s == Pr3Stage.HandlingReview) f.HandlingChiefId = id;
        else if (s == Pr3Stage.SafetyReview) f.SafetyChiefId = id;
    }

    private static void SetManager(Pr3Form f, Pr3Stage s, string? id)
    {
        if (s == Pr3Stage.HandlingReview) f.HandlingManagerId = id;
        else if (s == Pr3Stage.SafetyReview) f.SafetyManagerId = id;
    }

    private async Task NotifyAsync(Pr3Form form, Employee recipient, string actionLabel, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(recipient.Email)) return;
        var msg = await composer.ComposeAsync(form, recipient, actionLabel, ct);
        await email.SendAsync(msg, ct);
    }
}

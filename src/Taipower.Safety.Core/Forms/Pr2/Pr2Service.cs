using Taipower.Safety.Core.Common;
using Taipower.Safety.Core.Forms.Workflow;
using Taipower.Safety.Core.Org;

namespace Taipower.Safety.Core.Forms.Pr2;

public sealed record Pr2CreateRequest(
    string CheckType, DateTime CheckDate, int ProjectId, int ContractorId,
    string LeaderId, string? TeamMembers, string? ImproveGroupId, IReadOnlyList<Pr2Item> Items);

public sealed record Pr2ActionRequest(
    long FormId, WorkflowAction Action, string? Comment,
    string? SpecificPersonId = null, string? InformGroupId = null, DateTime? ImproveDeadline = null,
    bool IsJump = false, bool IsProxy = false, bool AgreeClose = true);

public sealed record Pr2ActionResult(bool Ok, string? Error = null, long FormId = 0);

/// <summary>
/// Pr2 建單與簽核的應用服務。與 Pr1 共用同一顆 <see cref="SafetyWorkflow"/> 狀態機
/// 與 <see cref="IAssigneeResolver"/> 指派解析器；本服務只負責 Pr2 表頭/旗標的讀寫與編排。
/// </summary>
public sealed class Pr2Service(
    IPr2Repository repo,
    IAssigneeResolver resolver,
    IEmailSender email,
    Pr2EmailComposer composer)
{
    public async Task<Pr2ActionResult> CreateAsync(Pr2CreateRequest req, CurrentUser actor, CancellationToken ct = default)
    {
        if (req.ProjectId <= 0 || req.ContractorId <= 0 || string.IsNullOrEmpty(req.LeaderId))
            return new Pr2ActionResult(false, "請完整填寫工程、承攬商與查核領隊。");
        if (!Pr2CheckType.Names.ContainsKey(req.CheckType))
            return new Pr2ActionResult(false, "查核類別不正確。");
        if (req.Items.Count == 0)
            return new Pr2ActionResult(false, "請至少填寫一項優點、缺點或建議改善事項。");

        var form = new Pr2Form
        {
            CheckType = req.CheckType,
            CheckDate = req.CheckDate,
            ProjectId = req.ProjectId,
            ContractorId = req.ContractorId,
            LeaderId = req.LeaderId,
            TeamMembers = req.TeamMembers,
            ImproveGroupId = req.ImproveGroupId,
            CreatedBy = actor.EmployeeId,
            Items = req.Items.ToList(),
        };

        // 建單 = C1 Submit → 工安組登記桌。
        var transition = SafetyWorkflow.Next(WorkflowStep.Draft, WorkflowAction.Submit, new WorkflowActionContext())!;
        var desk = await resolver.ResolveAsync(form, transition.Assignee, actor.EmployeeId, null, ct);
        if (desk is null) return new Pr2ActionResult(false, "找不到工安組登記桌，請先於組織維護設定。");

        form.Step = transition.To;
        form.CurrentHolderId = actor.EmployeeId;
        form.PendingRecipientId = desk.EmployeeId;

        // 單號在交易內重試，避開併發撞號。
        for (var attempt = 0; ; attempt++)
        {
            form.FormNumber = await repo.NextFormNumberAsync(req.CheckType, req.CheckDate, ct);
            try
            {
                var id = await repo.CreateAsync(form, actor.Name, ct);
                await NotifyAsync(form, desk, "建單", ct);
                return new Pr2ActionResult(true, FormId: id);
            }
            catch (Exception) when (attempt < 3)
            {
                // 可能是單號 UNIQUE 撞號，重算後重試。
            }
        }
    }

    public async Task<Pr2ActionResult> ActAsync(Pr2ActionRequest req, CurrentUser actor, CancellationToken ct = default)
    {
        var form = await repo.GetAsync(req.FormId, ct);
        if (form is null) return new Pr2ActionResult(false, "找不到此單。");
        if (form.Status != FormStatus.InProgress) return new Pr2ActionResult(false, "此單已結案或作廢，無法再操作。");

        // 授權：只有待辦收件人或目前持有人能操作。
        if (actor.EmployeeId != form.PendingRecipientId && actor.EmployeeId != form.CurrentHolderId)
            return new Pr2ActionResult(false, "您目前不是此單的處理人。");

        var ctx = new WorkflowActionContext { IsJump = req.IsJump, IsProxy = req.IsProxy, AgreeClose = req.AgreeClose };
        var transition = SafetyWorkflow.Next(form.Step, req.Action, ctx);
        if (transition is null) return new Pr2ActionResult(false, "目前狀態不允許這個動作。");

        // 本次動作要寫入的欄位（督促組別/期限/簽核人/旗標）。
        ApplySideData(form, req, ctx);

        Employee? assignee;
        if (transition.Assignee == AssigneeRule.NextApprover)
        {
            // C6：下一關的「位置」與「人員」須一起算 (去重可能跳層)，覆寫 To.Position。
            var (position, approver) = await resolver.ResolveApprovalNextAsync(form, ct);
            if (approver is null)
                return new Pr2ActionResult(false, "找不到下一關的批示長官，請確認副處長／處長設定。");
            transition = transition with { To = transition.To with { Position = position } };
            assignee = approver;
        }
        else
        {
            assignee = await resolver.ResolveAsync(form, transition.Assignee, actor.EmployeeId, req.SpecificPersonId, ct);
            if (assignee is null && transition.Assignee != AssigneeRule.None)
                return new Pr2ActionResult(false, "找不到下一關的處理人，請確認組織與人員設定。");
        }

        form.CurrentHolderId = actor.EmployeeId;
        form.PendingRecipientId = assignee?.EmployeeId;

        try
        {
            await repo.ApplyTransitionAsync(form, transition, actor.EmployeeId, actor.Name, req.Comment, ct);
        }
        catch (Exception ex) when (ex.GetType().Name == "Pr2ConcurrencyException")
        {
            return new Pr2ActionResult(false, ex.Message);
        }

        if (transition.SendEmail && assignee is not null)
            await NotifyAsync(form, assignee, transition.HistoryLabel ?? "簽核", ct);

        return new Pr2ActionResult(true, FormId: form.FormId);
    }

    /// <summary>收件人開啟待辦，承接為目前持有人（不改變步驟）。</summary>
    public async Task OpenAsync(long formId, CurrentUser actor, CancellationToken ct = default)
    {
        var form = await repo.GetAsync(formId, ct);
        if (form is null || form.PendingRecipientId != actor.EmployeeId || form.CurrentHolderId == actor.EmployeeId) return;
        var open = form.Step with { Transit = Transit.Working };
        form.CurrentHolderId = actor.EmployeeId;
        await repo.ApplyTransitionAsync(form, new WorkflowTransition(open, AssigneeRule.Self, false, HistoryLabel: "承接"),
            actor.EmployeeId, actor.Name, null, ct);
    }

    private static void ApplySideData(Pr2Form form, Pr2ActionRequest req, WorkflowActionContext ctx)
    {
        var stage = form.Step.Stage;

        if (req.Action is WorkflowAction.AssignChief)
            SetChief(form, stage, req.SpecificPersonId);
        else if (req.Action is WorkflowAction.AssignOfficer)
            SetOfficer(form, stage, req.SpecificPersonId);

        if (req.Action is WorkflowAction.Submit && form.Step.Position == Position.Officer)
        {
            SetOfficer(form, stage, form.CurrentHolderId);
            form.IsJump = ctx.IsJump;
            if (stage == Stage.SafetyReview)
            {
                form.InformGroupId = req.InformGroupId ?? form.InformGroupId;
                form.ImproveDeadline = req.ImproveDeadline ?? form.ImproveDeadline;
            }
        }
        if (req.Action is WorkflowAction.Submit && form.Step.Position == Position.Chief)
        {
            SetChief(form, stage, form.CurrentHolderId);
            form.IsProxy = ctx.IsProxy;
        }
        if (req.Action is WorkflowAction.Submit && form.Step.Position == Position.Manager)
            SetManager(form, stage, form.CurrentHolderId);

        if (req.Action is WorkflowAction.CloseDecision && !ctx.AgreeClose)
        {
            form.IsJump = false;
            form.IsProxy = false;
        }
    }

    private static void SetOfficer(Pr2Form f, Stage s, string? id)
    {
        if (s == Stage.SafetyReview) f.SafetyOfficerId = id;
        else if (s == Stage.Improvement) f.ImproveOfficerId = id;
        else if (s == Stage.Verification) f.VerifyOfficerId = id;
    }

    private static void SetChief(Pr2Form f, Stage s, string? id)
    {
        if (s == Stage.SafetyReview) f.SafetyChiefId = id;
        else if (s == Stage.Improvement) f.ImproveChiefId = id;
        else if (s == Stage.Verification) f.VerifyChiefId = id;
    }

    private static void SetManager(Pr2Form f, Stage s, string? id)
    {
        if (s == Stage.SafetyReview) f.SafetyManagerId = id;
        else if (s == Stage.Improvement) f.ImproveManagerId = id;
        else if (s == Stage.Verification) f.VerifyManagerId = id;
    }

    private async Task NotifyAsync(Pr2Form form, Employee recipient, string actionLabel, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(recipient.Email)) return;
        var msg = await composer.ComposeAsync(form, recipient, actionLabel, ct);
        await email.SendAsync(msg, ct);
    }
}

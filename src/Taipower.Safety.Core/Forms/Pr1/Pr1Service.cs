using Taipower.Safety.Core.Common;
using Taipower.Safety.Core.Org;

namespace Taipower.Safety.Core.Forms.Pr1;

public sealed record Pr1CreateRequest(
    string PatrolType, DateTime PatrolDate, int ProjectId, int ContractorId,
    string LeaderId, string? TeamMembers, string? ImproveGroupId, IReadOnlyList<Pr1Item> Items);

public sealed record Pr1ActionRequest(
    long FormId, Pr1Action Action, string? Comment,
    string? SpecificPersonId = null, string? InformGroupId = null, DateTime? ImproveDeadline = null,
    bool IsJump = false, bool IsProxy = false, bool AgreeClose = true);

public sealed record Pr1ActionResult(bool Ok, string? Error = null, long FormId = 0);

/// <summary>Pr1 建單與簽核的應用服務；把流程引擎、指派解析、持久化與通知串起來。</summary>
public sealed class Pr1Service(
    IPr1Repository repo,
    IAssigneeResolver resolver,
    IEmailSender email,
    Pr1EmailComposer composer,
    IFileStorage files,
    StorageOptions storage)
{
    public async Task<Pr1ActionResult> CreateAsync(Pr1CreateRequest req, CurrentUser actor, CancellationToken ct = default)
    {
        if (req.ProjectId <= 0 || req.ContractorId <= 0 || string.IsNullOrEmpty(req.LeaderId))
            return new Pr1ActionResult(false, "請完整填寫工程、承攬商與巡查領隊。");
        if (!PatrolType.Names.ContainsKey(req.PatrolType))
            return new Pr1ActionResult(false, "巡查類別不正確。");
        if (req.Items.Count == 0)
            return new Pr1ActionResult(false, "請至少填寫一項優點、缺點或建議改善事項。");

        var form = new Pr1Form
        {
            PatrolType = req.PatrolType,
            PatrolDate = req.PatrolDate,
            ProjectId = req.ProjectId,
            ContractorId = req.ContractorId,
            LeaderId = req.LeaderId,
            TeamMembers = req.TeamMembers,
            ImproveGroupId = req.ImproveGroupId,
            CreatedBy = actor.EmployeeId,
            Items = req.Items.ToList(),
        };

        // 建單 = C1 Submit → 工安組登記桌。
        var transition = Pr1Workflow.Next(Pr1Step.Draft, Pr1Action.Submit, new Pr1ActionContext())!;
        var desk = await resolver.ResolveAsync(form, transition.Assignee, actor.EmployeeId, null, ct);
        if (desk is null) return new Pr1ActionResult(false, "找不到工安組登記桌，請先於組織維護設定。");

        form.Step = transition.To;
        form.CurrentHolderId = actor.EmployeeId;
        form.PendingRecipientId = desk.EmployeeId;

        // 單號在交易內重試，避開併發撞號。
        for (var attempt = 0; ; attempt++)
        {
            form.FormNumber = await repo.NextFormNumberAsync(req.PatrolType, req.PatrolDate, ct);
            try
            {
                var id = await repo.CreateAsync(form, actor.Name, ct);
                await NotifyAsync(form, desk, "建單", ct);
                return new Pr1ActionResult(true, FormId: id);
            }
            catch (Exception) when (attempt < 3)
            {
                // 可能是單號 UNIQUE 撞號，重算後重試。
            }
        }
    }

    public async Task<Pr1ActionResult> ActAsync(Pr1ActionRequest req, CurrentUser actor, CancellationToken ct = default)
    {
        var form = await repo.GetAsync(req.FormId, ct);
        if (form is null) return new Pr1ActionResult(false, "找不到此單。");
        if (form.Status != FormStatus.InProgress) return new Pr1ActionResult(false, "此單已結案或作廢，無法再操作。");

        // 授權：只有待辦收件人或目前持有人能操作。
        if (actor.EmployeeId != form.PendingRecipientId && actor.EmployeeId != form.CurrentHolderId)
            return new Pr1ActionResult(false, "您目前不是此單的處理人。");

        var ctx = new Pr1ActionContext { IsJump = req.IsJump, IsProxy = req.IsProxy, AgreeClose = req.AgreeClose };
        var transition = Pr1Workflow.Next(form.Step, req.Action, ctx);
        if (transition is null) return new Pr1ActionResult(false, "目前狀態不允許這個動作。");

        // 本次動作要寫入的欄位（督促組別/期限/簽核人/旗標）。
        ApplySideData(form, req, ctx);

        Org.Employee? assignee;
        if (transition.Assignee == AssigneeRule.NextApprover)
        {
            // C6：下一關的「位置」與「人員」須一起算 (去重可能跳層)，覆寫 To.Position。
            var (position, approver) = await resolver.ResolveApprovalNextAsync(form, ct);
            if (approver is null)
                return new Pr1ActionResult(false, "找不到下一關的批示長官，請確認副處長／處長設定。");
            transition = transition with { To = transition.To with { Position = position } };
            assignee = approver;
        }
        else
        {
            assignee = await resolver.ResolveAsync(form, transition.Assignee, actor.EmployeeId, req.SpecificPersonId, ct);
            if (assignee is null && transition.Assignee != AssigneeRule.None)
                return new Pr1ActionResult(false, "找不到下一關的處理人，請確認組織與人員設定。");
        }

        form.CurrentHolderId = actor.EmployeeId;
        form.PendingRecipientId = assignee?.EmployeeId;

        try
        {
            await repo.ApplyTransitionAsync(form, transition, actor.EmployeeId, actor.Name, req.Comment, ct);
        }
        catch (Exception ex) when (ex.GetType().Name == "Pr1ConcurrencyException")
        {
            return new Pr1ActionResult(false, ex.Message);
        }

        if (transition.SendEmail && assignee is not null)
            await NotifyAsync(form, assignee, transition.HistoryLabel ?? "簽核", ct);

        return new Pr1ActionResult(true, FormId: form.FormId);
    }

    /// <summary>上傳一張項目照片（缺失現況 / 改善後）。僅目前處理人可於單進行中時上傳。</summary>
    public async Task<Pr1ActionResult> AddPhotoAsync(
        long formId, long itemId, string category, string originalName, long length, Stream content,
        CurrentUser actor, CancellationToken ct = default)
    {
        var form = await repo.GetAsync(formId, ct);
        if (form is null) return new Pr1ActionResult(false, "找不到此單。");
        if (form.Status != FormStatus.InProgress) return new Pr1ActionResult(false, "此單已結案或作廢，無法編修照片。");
        if (actor.EmployeeId != form.CurrentHolderId || actor.EmployeeId != form.PendingRecipientId)
            return new Pr1ActionResult(false, "您目前不是此單的處理人，無法上傳照片。");
        if (form.Items.All(i => i.ItemId != itemId))
            return new Pr1ActionResult(false, "找不到對應的巡查項目。");

        if (length <= 0) return new Pr1ActionResult(false, "檔案是空的。");
        if (length > storage.MaxFileBytes)
            return new Pr1ActionResult(false, $"檔案超過上限 {storage.MaxFileBytes / 1024 / 1024} MB。");

        var ext = Path.GetExtension(originalName)?.ToLowerInvariant();
        if (!ImageContent.IsAllowedExtension(ext))
            return new Pr1ActionResult(false, "僅允許 JPG / PNG / GIF / WebP 圖片。");
        if (!await LooksLikeImageAsync(content, ct))
            return new Pr1ActionResult(false, "檔案內容不是有效的圖片。");

        var stored = await files.SaveAsync($"pr1/{formId}", ext!, content, ct);
        await repo.AddAttachmentAsync(new Attachment
        {
            ItemId = itemId,
            Category = string.Equals(category, "After", StringComparison.OrdinalIgnoreCase) ? "After" : "Before",
            OriginalName = SafeName(originalName),
            StoredName = stored,
            ContentType = ImageContent.ContentTypeFor(ext!)!,
            SizeBytes = length,
            UploadedBy = actor.EmployeeId,
        }, ct);
        return new Pr1ActionResult(true, FormId: formId);
    }

    /// <summary>刪除一張項目照片。僅目前處理人可於單進行中時刪除。</summary>
    public async Task<Pr1ActionResult> DeletePhotoAsync(long attachmentId, CurrentUser actor, CancellationToken ct = default)
    {
        var info = await repo.GetAttachmentAsync(attachmentId, ct);
        if (info is null) return new Pr1ActionResult(false, "找不到照片。");
        var form = await repo.GetAsync(info.FormId, ct);
        if (form is null) return new Pr1ActionResult(false, "找不到此單。");
        if (form.Status != FormStatus.InProgress
            || actor.EmployeeId != form.CurrentHolderId || actor.EmployeeId != form.PendingRecipientId)
            return new Pr1ActionResult(false, "您目前無法刪除此照片。");

        await repo.DeleteAttachmentAsync(attachmentId, ct);
        files.Delete(info.StoredName);
        return new Pr1ActionResult(true, FormId: info.FormId);
    }

    /// <summary>
    /// 儲存逐項 C4 改善結果 / C5 查證結果。僅目前處理人（經辦關）可於改善/查證階段編修；
    /// 依目前階段只更新對應欄位，不改變流程步驟（上呈仍走 Submit）。
    /// </summary>
    public async Task<Pr1ActionResult> SaveItemResultsAsync(
        long formId, IReadOnlyList<Pr1ItemResult> results, CurrentUser actor, CancellationToken ct = default)
    {
        var form = await repo.GetAsync(formId, ct);
        if (form is null) return new Pr1ActionResult(false, "找不到此單。");
        if (form.Status != FormStatus.InProgress) return new Pr1ActionResult(false, "此單已結案或作廢，無法編修。");
        if (actor.EmployeeId != form.CurrentHolderId || actor.EmployeeId != form.PendingRecipientId)
            return new Pr1ActionResult(false, "您目前不是此單的處理人。");
        if (form.Step.Position != Position.Officer || form.Step.Stage is not (Stage.Improvement or Stage.Verification))
            return new Pr1ActionResult(false, "目前階段不可輸入改善 / 查證結果。");

        var itemIds = form.Items.Select(i => i.ItemId).ToHashSet();
        var valid = results.Where(r => itemIds.Contains(r.ItemId)).ToList();
        if (valid.Count == 0) return new Pr1ActionResult(false, "沒有可儲存的項目。");

        await repo.UpdateItemResultsAsync(formId, form.Step.Stage, valid, ct);
        return new Pr1ActionResult(true, FormId: formId);
    }

    /// <summary>取用一張照片的串流以供顯示/下載（檢視授權由控制器的功能權限把關）。</summary>
    public async Task<(Stream Stream, string ContentType, string FileName)?> OpenPhotoAsync(long attachmentId, CancellationToken ct = default)
    {
        var info = await repo.GetAttachmentAsync(attachmentId, ct);
        if (info is null) return null;
        var stream = await files.OpenReadAsync(info.StoredName, ct);
        return stream is null ? null : (stream, info.ContentType, info.OriginalName);
    }

    private static async Task<bool> LooksLikeImageAsync(Stream content, CancellationToken ct)
    {
        if (!content.CanSeek) return true; // 無法預讀時，已有副檔名把關
        var head = new byte[12];
        var read = await content.ReadAsync(head.AsMemory(0, 12), ct);
        content.Position = 0;
        return ImageContent.LooksLikeImage(head.AsSpan(0, read));
    }

    private static string SafeName(string name)
    {
        var n = Path.GetFileName(name.Trim());
        return n.Length > 200 ? n[^200..] : n;
    }

    /// <summary>收件人開啟待辦，承接為目前持有人（不改變步驟）。</summary>
    public async Task OpenAsync(long formId, CurrentUser actor, CancellationToken ct = default)
    {
        var form = await repo.GetAsync(formId, ct);
        if (form is null || form.PendingRecipientId != actor.EmployeeId || form.CurrentHolderId == actor.EmployeeId) return;
        var open = form.Step with { Transit = Transit.Working };
        form.CurrentHolderId = actor.EmployeeId;
        await repo.ApplyTransitionAsync(form, new Pr1Transition(open, AssigneeRule.Self, false, HistoryLabel: "承接"),
            actor.EmployeeId, actor.Name, null, ct);
    }

    private static void ApplySideData(Pr1Form form, Pr1ActionRequest req, Pr1ActionContext ctx)
    {
        var stage = form.Step.Stage;

        if (req.Action is Pr1Action.AssignChief)
            SetChief(form, stage, req.SpecificPersonId);
        else if (req.Action is Pr1Action.AssignOfficer)
            SetOfficer(form, stage, req.SpecificPersonId);

        if (req.Action is Pr1Action.Submit && form.Step.Position == Position.Officer)
        {
            SetOfficer(form, stage, form.CurrentHolderId);
            form.IsJump = ctx.IsJump;
            if (stage == Stage.SafetyReview)
            {
                form.InformGroupId = req.InformGroupId ?? form.InformGroupId;
                form.ImproveDeadline = req.ImproveDeadline ?? form.ImproveDeadline;
            }
        }
        if (req.Action is Pr1Action.Submit && form.Step.Position == Position.Chief)
        {
            SetChief(form, stage, form.CurrentHolderId);
            form.IsProxy = ctx.IsProxy;
        }
        if (req.Action is Pr1Action.Submit && form.Step.Position == Position.Manager)
            SetManager(form, stage, form.CurrentHolderId);

        if (req.Action is Pr1Action.CloseDecision && !ctx.AgreeClose)
        {
            form.IsJump = false;
            form.IsProxy = false;
        }
    }

    private static void SetOfficer(Pr1Form f, Stage s, string? id)
    {
        if (s == Stage.SafetyReview) f.SafetyOfficerId = id;
        else if (s == Stage.Improvement) f.ImproveOfficerId = id;
        else if (s == Stage.Verification) f.VerifyOfficerId = id;
    }

    private static void SetChief(Pr1Form f, Stage s, string? id)
    {
        if (s == Stage.SafetyReview) f.SafetyChiefId = id;
        else if (s == Stage.Improvement) f.ImproveChiefId = id;
        else if (s == Stage.Verification) f.VerifyChiefId = id;
    }

    private static void SetManager(Pr1Form f, Stage s, string? id)
    {
        if (s == Stage.SafetyReview) f.SafetyManagerId = id;
        else if (s == Stage.Improvement) f.ImproveManagerId = id;
        else if (s == Stage.Verification) f.VerifyManagerId = id;
    }

    private async Task NotifyAsync(Pr1Form form, Employee recipient, string actionLabel, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(recipient.Email)) return;
        var msg = await composer.ComposeAsync(form, recipient, actionLabel, ct);
        await email.SendAsync(msg, ct);
    }
}

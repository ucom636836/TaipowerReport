using System.Text;
using Taipower.Safety.Core.Common;
using Taipower.Safety.Core.Reference;

namespace Taipower.Safety.Core.Forms.Pr5;

public sealed record Pr5CreateRequest(
    Pr5Source Source, long? SourceFormId, long? SourceItemId,
    int ProjectId, int ContractorId, string? AuditGroupId,
    int? HazardId, int? DeficiencyTypeId, int? DeficiencyItemId,
    DateTime? ViolationDate, string? Place, string? UnitPeriod,
    string? PenaltyItemCode, decimal? PenaltyAmount, DateTime? ImproveDeadline, string? Remark,
    bool Notify = true);

public sealed record Pr5ActionResult(bool Ok, string? Error = null, long PenaltyId = 0);

/// <summary>Pr5 罰單的開立、作廢、簽收與承攬商通知。</summary>
public sealed class Pr5Service(IPr5Repository repo, IReferenceRepository refs, IEmailSender email)
{
    public async Task<Pr5ActionResult> CreateAsync(Pr5CreateRequest req, CurrentUser actor, CancellationToken ct = default)
    {
        if (req.ProjectId <= 0 || req.ContractorId <= 0)
            return new Pr5ActionResult(false, "請選擇受罰工程與承攬商。");
        if (req.PenaltyAmount is < 0)
            return new Pr5ActionResult(false, "罰款金額不可為負。");

        var penalty = new Pr5Penalty
        {
            Source = req.Source,
            SourceFormId = req.SourceFormId,
            SourceItemId = req.SourceItemId,
            ProjectId = req.ProjectId,
            ContractorId = req.ContractorId,
            AuditGroupId = req.AuditGroupId,
            HazardId = req.HazardId,
            DeficiencyTypeId = req.DeficiencyTypeId,
            DeficiencyItemId = req.DeficiencyItemId,
            ViolationDate = req.ViolationDate,
            Place = req.Place,
            UnitPeriod = req.UnitPeriod,
            PenaltyItemCode = req.PenaltyItemCode,
            PenaltyAmount = req.PenaltyAmount,
            ImproveDeadline = req.ImproveDeadline,
            Remark = req.Remark,
            CreatedBy = actor.EmployeeId,
        };

        long id = 0;
        for (var attempt = 0; ; attempt++)
        {
            penalty.PenaltyNumber = await repo.NextNumberAsync(DateTime.Now, ct);
            try
            {
                id = await repo.CreateAsync(penalty, ct);
                break;
            }
            catch (Exception) when (attempt < 3)
            {
                // 單號 UNIQUE 撞號，重算後重試。
            }
        }

        // 回寫 Pr1 缺失項的罰單號（建立缺失 ↔ 罰單連結）。
        if (req.Source == Pr5Source.Pr1 && req.SourceItemId is { } itemId)
            await repo.LinkPr1ItemAsync(itemId, penalty.PenaltyNumber, ct);

        if (req.Notify)
            await NotifyContractorAsync(penalty, id, ct);

        return new Pr5ActionResult(true, PenaltyId: id);
    }

    public async Task<Pr5ActionResult> VoidAsync(long penaltyId, CurrentUser actor, CancellationToken ct = default)
        => await ChangeStatusAsync(penaltyId, Pr5Status.Void, "作廢", ct);

    public async Task<Pr5ActionResult> MarkReceivedAsync(long penaltyId, CurrentUser actor, CancellationToken ct = default)
        => await ChangeStatusAsync(penaltyId, Pr5Status.Received, "簽收", ct);

    /// <summary>重寄承攬商通知信（例如新增聯絡人後補寄）。</summary>
    public async Task<Pr5ActionResult> NotifyAsync(long penaltyId, CurrentUser actor, CancellationToken ct = default)
    {
        var penalty = await repo.GetAsync(penaltyId, ct);
        if (penalty is null) return new Pr5ActionResult(false, "找不到此罰單。");
        if (penalty.Status == Pr5Status.Void) return new Pr5ActionResult(false, "已作廢的罰單不再通知。");
        var sent = await NotifyContractorAsync(penalty, penaltyId, ct);
        return sent
            ? new Pr5ActionResult(true, PenaltyId: penaltyId)
            : new Pr5ActionResult(false, "此承攬商沒有可通知的聯絡信箱。");
    }

    private async Task<Pr5ActionResult> ChangeStatusAsync(long penaltyId, Pr5Status status, string label, CancellationToken ct)
    {
        var penalty = await repo.GetAsync(penaltyId, ct);
        if (penalty is null) return new Pr5ActionResult(false, "找不到此罰單。");
        if (penalty.Status == status) return new Pr5ActionResult(false, $"此罰單已是{label}狀態。");
        if (penalty.Status == Pr5Status.Void) return new Pr5ActionResult(false, "已作廢的罰單無法再變更。");

        var ok = await repo.SetStatusAsync(penaltyId, status, penalty.RowVersion, ct);
        return ok
            ? new Pr5ActionResult(true, PenaltyId: penaltyId)
            : new Pr5ActionResult(false, "此罰單已被其他人變更，請重新整理後再試。");
    }

    private async Task<bool> NotifyContractorAsync(Pr5Penalty penalty, long penaltyId, CancellationToken ct)
    {
        var contractor = await refs.GetContractorAsync(penalty.ContractorId, ct);
        var recipients = contractor?.Contacts
            .Select(c => c.Email)
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Distinct()
            .ToList() ?? [];
        if (recipients.Count == 0) return false;

        var project = await refs.GetProjectAsync(penalty.ProjectId, ct);
        var body = new StringBuilder();
        body.Append("<p>貴公司於下列工程經查有工安缺失，茲開立工安缺失改善通知單（罰單），請查照並限期改善。</p><ul>");
        body.Append($"<li>罰單編號：{penalty.PenaltyNumber}</li>");
        body.Append($"<li>工程名稱：{project?.ProjectNumber} {project?.Name}</li>");
        body.Append($"<li>承攬商：{contractor!.Name}</li>");
        if (penalty.ViolationDate is not null) body.Append($"<li>違規日期：{penalty.ViolationDate:yyyy-MM-dd}</li>");
        if (!string.IsNullOrWhiteSpace(penalty.Place)) body.Append($"<li>違規地點：{penalty.Place}</li>");
        if (penalty.PenaltyAmount is not null) body.Append($"<li>罰款金額：NT$ {penalty.PenaltyAmount:#,0}</li>");
        if (penalty.ImproveDeadline is not null) body.Append($"<li>改善期限：{penalty.ImproveDeadline:yyyy-MM-dd}</li>");
        if (!string.IsNullOrWhiteSpace(penalty.Remark)) body.Append($"<li>備註：{penalty.Remark}</li>");
        body.Append("</ul>");

        await email.SendAsync(new EmailMessage(
            To: recipients!, Cc: [],
            Subject: $"[工安缺失改善通知單] {penalty.PenaltyNumber}",
            HtmlBody: body.ToString()), ct);

        await repo.SetNotifiedAsync(penaltyId, ct);
        return true;
    }
}

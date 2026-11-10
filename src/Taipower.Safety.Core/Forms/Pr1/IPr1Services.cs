using Taipower.Safety.Core.Org;

using Taipower.Safety.Core.Forms.Workflow;
namespace Taipower.Safety.Core.Forms.Pr1;

public interface IPr1Repository
{
    Task<string> NextFormNumberAsync(string patrolType, DateTime date, CancellationToken ct = default);
    Task<long> CreateAsync(Pr1Form form, string actorTitle, CancellationToken ct = default);
    Task<Pr1Form?> GetAsync(long formId, CancellationToken ct = default);
    Task<Pr1Form?> GetByNumberAsync(string formNumber, CancellationToken ct = default);
    Task ApplyTransitionAsync(Pr1Form form, WorkflowTransition transition, string actorId, string actorTitle, string? comment, CancellationToken ct = default);
    Task<IReadOnlyList<Pr1InboxRow>> GetInboxAsync(string employeeId, CancellationToken ct = default);
    Task<IReadOnlyList<Pr1InboxRow>> SearchAsync(Pr1SearchQuery query, string employeeId, bool allScopes, CancellationToken ct = default);
    Task<IReadOnlyList<WorkflowHistoryRow>> GetHistoryAsync(long formId, CancellationToken ct = default);

    // 項目照片（缺失現況 / 改善後）
    Task<long> AddAttachmentAsync(Attachment attachment, CancellationToken ct = default);
    Task<Pr1AttachmentInfo?> GetAttachmentAsync(long attachmentId, CancellationToken ct = default);
    Task DeleteAttachmentAsync(long attachmentId, CancellationToken ct = default);

    // 逐項 C4 改善結果 / C5 查證結果（依 stage 只更新對應欄位）
    Task UpdateItemResultsAsync(long formId, Stage stage, IReadOnlyList<Pr1ItemResult> results, CancellationToken ct = default);
}

/// <summary>一個缺失項目的 C4 改善 / C5 查證輸入。依 stage 只取對應欄位。</summary>
public sealed record Pr1ItemResult(
    long ItemId,
    bool? Improved, string? NotImprovedReason, DateTime? ImprovedDate,
    bool? Verified, string? VerifyReason);

/// <summary>一張照片的中繼資料，含其所屬表單（供授權與供檔）。</summary>
public sealed record Pr1AttachmentInfo(
    long AttachmentId, long ItemId, long FormId, string Category,
    string OriginalName, string StoredName, string ContentType);

public sealed class Pr1SearchQuery
{
    public int? ProjectId { get; set; }
    public int? ContractorId { get; set; }
    public string? LeaderId { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
}

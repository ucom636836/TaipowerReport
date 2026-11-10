using Taipower.Safety.Core.Forms.Workflow;

namespace Taipower.Safety.Core.Forms.Pr2;

public interface IPr2Repository
{
    Task<string> NextFormNumberAsync(string checkType, DateTime date, CancellationToken ct = default);
    Task<long> CreateAsync(Pr2Form form, string actorTitle, CancellationToken ct = default);
    Task<Pr2Form?> GetAsync(long formId, CancellationToken ct = default);
    Task<Pr2Form?> GetByNumberAsync(string formNumber, CancellationToken ct = default);
    Task ApplyTransitionAsync(Pr2Form form, WorkflowTransition transition, string actorId, string actorTitle, string? comment, CancellationToken ct = default);
    Task<IReadOnlyList<Pr2InboxRow>> GetInboxAsync(string employeeId, CancellationToken ct = default);
    Task<IReadOnlyList<Pr2InboxRow>> SearchAsync(Pr2SearchQuery query, string employeeId, bool allScopes, CancellationToken ct = default);
    Task<IReadOnlyList<WorkflowHistoryRow>> GetHistoryAsync(long formId, CancellationToken ct = default);
}

public sealed class Pr2SearchQuery
{
    public int? ProjectId { get; set; }
    public int? ContractorId { get; set; }
    public string? LeaderId { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
}

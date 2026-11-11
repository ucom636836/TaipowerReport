using Taipower.Safety.Core.Forms.Workflow;
using Taipower.Safety.Core.Org;

namespace Taipower.Safety.Core.Forms.Pr4;

/// <summary>把 Pr4 流程決定的 <see cref="Pr4AssigneeRule"/> 解析成實際員工。</summary>
public interface IPr4AssigneeResolver
{
    Task<Employee?> ResolveAsync(Pr4Form form, Pr4AssigneeRule rule, string actorId, string? specificPersonId, CancellationToken ct = default);
}

public interface IPr4Repository
{
    Task<string> NextFormNumberAsync(DateTime date, CancellationToken ct = default);
    Task<long> CreateAsync(Pr4Form form, string actorTitle, CancellationToken ct = default);
    Task<Pr4Form?> GetAsync(long formId, CancellationToken ct = default);
    Task<Pr4Form?> GetByNumberAsync(string formNumber, CancellationToken ct = default);
    Task ApplyTransitionAsync(Pr4Form form, Pr4Transition transition, string actorId, string actorTitle, string? comment, CancellationToken ct = default);
    Task<IReadOnlyList<Pr4InboxRow>> GetInboxAsync(string employeeId, CancellationToken ct = default);
    Task<IReadOnlyList<Pr4InboxRow>> SearchAsync(Pr4SearchQuery query, string employeeId, bool allScopes, CancellationToken ct = default);
    Task<IReadOnlyList<WorkflowHistoryRow>> GetHistoryAsync(long formId, CancellationToken ct = default);
}

public sealed class Pr4SearchQuery
{
    public int? ProjectId { get; set; }
    public int? ContractorId { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
}

using Taipower.Safety.Core.Forms.Workflow;
using Taipower.Safety.Core.Org;

namespace Taipower.Safety.Core.Forms.Pr3;

/// <summary>把 Pr3 流程決定的 <see cref="Pr3AssigneeRule"/> 解析成實際員工（Pr3 的動態組別路由自成一套）。</summary>
public interface IPr3AssigneeResolver
{
    Task<Employee?> ResolveAsync(Pr3Form form, Pr3AssigneeRule rule, string actorId, string? specificPersonId, CancellationToken ct = default);
}

public interface IPr3Repository
{
    Task<string> NextFormNumberAsync(DateTime date, CancellationToken ct = default);
    Task<long> CreateAsync(Pr3Form form, string actorTitle, CancellationToken ct = default);
    Task<Pr3Form?> GetAsync(long formId, CancellationToken ct = default);
    Task<Pr3Form?> GetByNumberAsync(string formNumber, CancellationToken ct = default);
    Task ApplyTransitionAsync(Pr3Form form, Pr3Transition transition, string actorId, string actorTitle, string? comment, CancellationToken ct = default);
    Task<IReadOnlyList<Pr3InboxRow>> GetInboxAsync(string employeeId, CancellationToken ct = default);
    Task<IReadOnlyList<Pr3InboxRow>> SearchAsync(Pr3SearchQuery query, string employeeId, bool allScopes, CancellationToken ct = default);
    Task<IReadOnlyList<WorkflowHistoryRow>> GetHistoryAsync(long formId, CancellationToken ct = default);
}

public sealed class Pr3SearchQuery
{
    public int? ProjectId { get; set; }
    public int? ContractorId { get; set; }
    public string? InspectorId { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
}

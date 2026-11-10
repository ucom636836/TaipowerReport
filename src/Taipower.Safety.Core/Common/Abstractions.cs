using Taipower.Safety.Core.Org;
using Taipower.Safety.Core.Security;

namespace Taipower.Safety.Core.Common;

public interface IClock
{
    DateTime Now { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime Now => DateTime.Now;
}

/// <summary>The authenticated user performing an operation.</summary>
public sealed record CurrentUser(
    string EmployeeId,
    string Name,
    string? GroupId,
    string? SectionCode,
    int? RankId,
    int? RoleId,
    IReadOnlySet<int> Features)
{
    public bool Can(int featureId) => Features.Contains(featureId);
}

public sealed record AuditEntry(string? EmployeeId, string? EmployeeName, string Action, string? Target, string? Message, bool Success, string? IpAddress);

public interface IAuditLog
{
    Task WriteAsync(AuditEntry entry, CancellationToken ct = default);
}

public sealed record EmailMessage(IReadOnlyList<string> To, IReadOnlyList<string> Cc, string Subject, string HtmlBody);

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}

public sealed record UserAccountRecord(string Account, string EmployeeId, string PasswordHash, bool IsActive, int FailedLoginCount, DateTime? LockoutUntil);

public interface IAccountRepository
{
    Task<UserAccountRecord?> FindAsync(string account, CancellationToken ct = default);
    Task RecordLoginSuccessAsync(string account, string? newPasswordHash, CancellationToken ct = default);
    Task RecordLoginFailureAsync(string account, int maxAttempts, TimeSpan lockout, CancellationToken ct = default);
    Task SetPasswordAsync(string account, string passwordHash, CancellationToken ct = default);
    Task CreateAsync(string account, string employeeId, string passwordHash, CancellationToken ct = default);
}

public interface IOrgRepository
{
    Task<IReadOnlyList<OrgGroup>> GetGroupsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<OrgSection>> GetSectionsAsync(string? groupId = null, CancellationToken ct = default);
    Task<IReadOnlyList<OrgRank>> GetRanksAsync(CancellationToken ct = default);
    Task<IReadOnlyList<OrgRole>> GetRolesAsync(CancellationToken ct = default);
    Task SaveGroupAsync(OrgGroup group, CancellationToken ct = default);
    Task SaveSectionAsync(OrgSection section, CancellationToken ct = default);
    Task SaveRankAsync(OrgRank rank, CancellationToken ct = default);
    Task SaveRoleAsync(OrgRole role, CancellationToken ct = default);

    Task<Employee?> GetEmployeeAsync(string employeeId, CancellationToken ct = default);
    Task<IReadOnlyList<Employee>> FindEmployeesAsync(EmployeeQuery query, CancellationToken ct = default);
    Task SaveEmployeeAsync(Employee employee, string actorId, CancellationToken ct = default);
}

public interface IPermissionRepository
{
    Task<IReadOnlyList<Feature>> GetFeaturesAsync(CancellationToken ct = default);
    Task<IReadOnlySet<int>> GetRoleFeaturesAsync(int roleId, CancellationToken ct = default);
    Task SetRoleFeaturesAsync(int roleId, IEnumerable<int> featureIds, CancellationToken ct = default);
}

public sealed class AuditLogQuery
{
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public string? Keyword { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

public sealed record AuditLogRow(long AuditLogId, DateTime OccurredAt, string? EmployeeId, string? EmployeeName, string Action, string? Target, string? Message, bool Success, string? IpAddress);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize)
{
    public int PageCount => PageSize <= 0 ? 1 : Math.Max(1, (Total + PageSize - 1) / PageSize);
}

public interface IAuditLogQuery
{
    Task<PagedResult<AuditLogRow>> SearchAsync(AuditLogQuery query, CancellationToken ct = default);
}

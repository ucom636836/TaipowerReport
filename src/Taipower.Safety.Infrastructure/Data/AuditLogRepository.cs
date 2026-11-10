using System.Text;
using Dapper;
using Microsoft.Extensions.Logging;
using Taipower.Safety.Core.Common;

namespace Taipower.Safety.Infrastructure.Data;

public sealed class AuditLogRepository(IDbConnectionFactory db, ILogger<AuditLogRepository> logger) : IAuditLog, IAuditLogQuery
{
    public async Task WriteAsync(AuditEntry e, CancellationToken ct = default)
    {
        try
        {
            await using var conn = await db.OpenAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT dbo.AuditLog (EmployeeId, EmployeeName, Action, Target, Message, Success, IpAddress)
                VALUES (@EmployeeId, @EmployeeName, @Action, @Target, @Message, @Success, @IpAddress)
                """, new
            {
                e.EmployeeId,
                e.EmployeeName,
                Action = Truncate(e.Action, 50),
                Target = Truncate(e.Target, 100),
                Message = Truncate(e.Message, 2000),
                e.Success,
                e.IpAddress,
            }, cancellationToken: ct));
        }
        catch (Exception ex)
        {
            // Auditing must never break the business operation, but it must not vanish either.
            logger.LogError(ex, "Failed to write audit log {Action} {Target}: {Message}", e.Action, e.Target, e.Message);
        }
    }

    public async Task<PagedResult<AuditLogRow>> SearchAsync(AuditLogQuery q, CancellationToken ct = default)
    {
        var where = new StringBuilder(" WHERE 1 = 1");
        if (q.From is not null) where.Append(" AND OccurredAt >= @From");
        if (q.To is not null) where.Append(" AND OccurredAt < DATEADD(DAY, 1, @To)");
        if (!string.IsNullOrWhiteSpace(q.Keyword))
            where.Append(" AND (EmployeeId LIKE @Like OR EmployeeName LIKE @Like OR Action LIKE @Like OR Target LIKE @Like OR Message LIKE @Like)");

        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 10, 200);
        var args = new { q.From, To = q.To?.Date, Like = "%" + OrgRepository.EscapeLike(q.Keyword) + "%", Skip = (page - 1) * size, Take = size };

        await using var conn = await db.OpenAsync(ct);
        var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM dbo.AuditLog" + where, args, cancellationToken: ct));
        var rows = await conn.QueryAsync<AuditLogRow>(new CommandDefinition(
            "SELECT AuditLogId, OccurredAt, EmployeeId, EmployeeName, Action, Target, Message, Success, IpAddress FROM dbo.AuditLog"
            + where + " ORDER BY OccurredAt DESC, AuditLogId DESC OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY", args, cancellationToken: ct));
        return new PagedResult<AuditLogRow>(rows.AsList(), total, page, size);
    }

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}

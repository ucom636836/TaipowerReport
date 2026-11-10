using Dapper;
using Taipower.Safety.Core.Common;

namespace Taipower.Safety.Infrastructure.Data;

public sealed class AccountRepository(IDbConnectionFactory db) : IAccountRepository
{
    public async Task<UserAccountRecord?> FindAsync(string account, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<UserAccountRecord>(new CommandDefinition(
            """
            SELECT a.Account, a.EmployeeId, a.PasswordHash,
                   CAST(CASE WHEN a.IsActive = 1 AND e.IsActive = 1 THEN 1 ELSE 0 END AS bit) AS IsActive,
                   a.FailedLoginCount, a.LockoutUntil
            FROM dbo.UserAccount a JOIN dbo.Employee e ON e.EmployeeId = a.EmployeeId
            WHERE a.Account = @account
            """, new { account }, cancellationToken: ct));
    }

    public async Task RecordLoginSuccessAsync(string account, string? newPasswordHash, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.UserAccount
               SET FailedLoginCount = 0, LockoutUntil = NULL, LastLoginAt = SYSDATETIME(),
                   PasswordHash = COALESCE(@newPasswordHash, PasswordHash)
             WHERE Account = @account
            """, new { account, newPasswordHash }, cancellationToken: ct));
    }

    public async Task RecordLoginFailureAsync(string account, int maxAttempts, TimeSpan lockout, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.UserAccount
               SET FailedLoginCount = FailedLoginCount + 1,
                   LockoutUntil = CASE WHEN FailedLoginCount + 1 >= @maxAttempts
                                       THEN DATEADD(SECOND, @seconds, SYSDATETIME()) ELSE LockoutUntil END
             WHERE Account = @account
            """, new { account, maxAttempts, seconds = (int)lockout.TotalSeconds }, cancellationToken: ct));
    }

    public async Task SetPasswordAsync(string account, string passwordHash, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.UserAccount
               SET PasswordHash = @passwordHash, PasswordChangedAt = SYSDATETIME(), FailedLoginCount = 0, LockoutUntil = NULL
             WHERE Account = @account
            """, new { account, passwordHash }, cancellationToken: ct));
    }

    public async Task CreateAsync(string account, string employeeId, string passwordHash, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT dbo.UserAccount (Account, EmployeeId, PasswordHash, PasswordChangedAt) VALUES (@account, @employeeId, @passwordHash, SYSDATETIME())",
            new { account, employeeId, passwordHash }, cancellationToken: ct));
    }
}

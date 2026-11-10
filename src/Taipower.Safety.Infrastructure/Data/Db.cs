using System.Data;
using DbUp;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Taipower.Safety.Infrastructure.Data;

public sealed class DatabaseOptions
{
    public const string Section = "Database";
    /// <summary>Use a least-privilege login (db_datareader/db_datawriter), never sa.</summary>
    public string ConnectionString { get; set; } = "";
}

public interface IDbConnectionFactory
{
    Task<SqlConnection> OpenAsync(CancellationToken ct = default);
}

public sealed class SqlConnectionFactory(IOptions<DatabaseOptions> options) : IDbConnectionFactory
{
    private readonly string _cs = options.Value.ConnectionString;

    public async Task<SqlConnection> OpenAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_cs))
            throw new InvalidOperationException("Database:ConnectionString is not configured.");
        var conn = new SqlConnection(_cs);
        await conn.OpenAsync(ct);
        return conn;
    }
}

/// <summary>Applies the embedded V###__*.sql scripts in order (tracked in dbo.SchemaVersions).</summary>
public static class DatabaseMigrator
{
    public static (bool Success, string? Error, IReadOnlyList<string> Applied) Upgrade(string connectionString, bool ensureDatabase = false)
    {
        if (ensureDatabase) EnsureDatabase.For.SqlDatabase(connectionString);

        var upgrader = DeployChanges.To
            .SqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(typeof(DatabaseMigrator).Assembly, name => name.Contains(".Migrations.Scripts."))
            .WithTransactionPerScript()
            .LogToConsole()
            .Build();

        var result = upgrader.PerformUpgrade();
        return (result.Successful, result.Error?.Message, result.Scripts.Select(s => s.Name).ToList());
    }
}

internal static class DbExtensions
{
    public static async Task InTransactionAsync(this SqlConnection conn, Func<IDbTransaction, Task> work)
    {
        using var tx = conn.BeginTransaction();
        try
        {
            await work(tx);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }
}

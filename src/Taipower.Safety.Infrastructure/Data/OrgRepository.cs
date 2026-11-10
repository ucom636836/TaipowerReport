using System.Text;
using Dapper;
using Taipower.Safety.Core.Common;
using Taipower.Safety.Core.Org;
using Taipower.Safety.Core.Security;

namespace Taipower.Safety.Infrastructure.Data;

public sealed class OrgRepository(IDbConnectionFactory db) : IOrgRepository
{
    private const string EmployeeSelect = """
        SELECT e.EmployeeId, e.Name, e.Email, e.GroupId, g.Name AS GroupName, e.SectionCode, s.Name AS SectionName,
               e.RankId, r.Name AS RankName, e.RoleId, ro.Name AS RoleName, e.IsActive, a.Account
        FROM dbo.Employee e
        LEFT JOIN dbo.OrgGroup g    ON g.GroupId = e.GroupId
        LEFT JOIN dbo.OrgSection s  ON s.GroupId = e.GroupId AND s.SectionCode = e.SectionCode
        LEFT JOIN dbo.OrgRank r     ON r.RankId = e.RankId
        LEFT JOIN dbo.OrgRole ro    ON ro.RoleId = e.RoleId
        LEFT JOIN dbo.UserAccount a ON a.EmployeeId = e.EmployeeId
        """;

    public async Task<IReadOnlyList<OrgGroup>> GetGroupsAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<OrgGroup>(new CommandDefinition(
            "SELECT GroupId, Name, ParentGroupId, SortOrder FROM dbo.OrgGroup ORDER BY SortOrder, GroupId", cancellationToken: ct))).AsList();
    }

    public async Task<IReadOnlyList<OrgSection>> GetSectionsAsync(string? groupId = null, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<OrgSection>(new CommandDefinition(
            "SELECT GroupId, SectionCode, Name FROM dbo.OrgSection WHERE (@groupId IS NULL OR GroupId = @groupId) ORDER BY GroupId, SectionCode",
            new { groupId }, cancellationToken: ct))).AsList();
    }

    public async Task<IReadOnlyList<OrgRank>> GetRanksAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<OrgRank>(new CommandDefinition(
            "SELECT RankId, Name, Level FROM dbo.OrgRank ORDER BY Level", cancellationToken: ct))).AsList();
    }

    public async Task<IReadOnlyList<OrgRole>> GetRolesAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<OrgRole>(new CommandDefinition(
            "SELECT RoleId, Name FROM dbo.OrgRole ORDER BY RoleId", cancellationToken: ct))).AsList();
    }

    public async Task SaveGroupAsync(OrgGroup group, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            MERGE dbo.OrgGroup AS t USING (SELECT @GroupId AS GroupId) AS s ON t.GroupId = s.GroupId
            WHEN MATCHED THEN UPDATE SET Name = @Name, ParentGroupId = @ParentGroupId, SortOrder = @SortOrder
            WHEN NOT MATCHED THEN INSERT (GroupId, Name, ParentGroupId, SortOrder) VALUES (@GroupId, @Name, @ParentGroupId, @SortOrder);
            """, group, cancellationToken: ct));
    }

    public async Task SaveSectionAsync(OrgSection section, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            MERGE dbo.OrgSection AS t USING (SELECT @GroupId AS GroupId, @SectionCode AS SectionCode) AS s
               ON t.GroupId = s.GroupId AND t.SectionCode = s.SectionCode
            WHEN MATCHED THEN UPDATE SET Name = @Name
            WHEN NOT MATCHED THEN INSERT (GroupId, SectionCode, Name) VALUES (@GroupId, @SectionCode, @Name);
            """, section, cancellationToken: ct));
    }

    public async Task SaveRankAsync(OrgRank rank, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            MERGE dbo.OrgRank AS t USING (SELECT @RankId AS RankId) AS s ON t.RankId = s.RankId
            WHEN MATCHED THEN UPDATE SET Name = @Name, Level = @Level
            WHEN NOT MATCHED THEN INSERT (RankId, Name, Level) VALUES (@RankId, @Name, @Level);
            """, rank, cancellationToken: ct));
    }

    public async Task SaveRoleAsync(OrgRole role, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            MERGE dbo.OrgRole AS t USING (SELECT @RoleId AS RoleId) AS s ON t.RoleId = s.RoleId
            WHEN MATCHED THEN UPDATE SET Name = @Name
            WHEN NOT MATCHED THEN INSERT (RoleId, Name) VALUES (@RoleId, @Name);
            """, role, cancellationToken: ct));
    }

    public async Task<Employee?> GetEmployeeAsync(string employeeId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Employee>(new CommandDefinition(
            EmployeeSelect + " WHERE e.EmployeeId = @employeeId", new { employeeId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<Employee>> FindEmployeesAsync(EmployeeQuery q, CancellationToken ct = default)
    {
        var sql = new StringBuilder(EmployeeSelect).Append(" WHERE 1 = 1");
        if (!q.IncludeInactive) sql.Append(" AND e.IsActive = 1");
        if (!string.IsNullOrWhiteSpace(q.GroupId)) sql.Append(" AND e.GroupId = @GroupId");
        if (q.RankId is not null) sql.Append(" AND e.RankId = @RankId");
        if (q.RoleId is not null) sql.Append(" AND e.RoleId = @RoleId");
        if (!string.IsNullOrWhiteSpace(q.Keyword)) sql.Append(" AND (e.EmployeeId LIKE @Like OR e.Name LIKE @Like OR a.Account LIKE @Like)");
        sql.Append(" ORDER BY e.GroupId, e.RankId, e.EmployeeId");

        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<Employee>(new CommandDefinition(sql.ToString(), new
        {
            q.GroupId,
            q.RankId,
            q.RoleId,
            Like = "%" + EscapeLike(q.Keyword) + "%",
        }, cancellationToken: ct))).AsList();
    }

    public async Task SaveEmployeeAsync(Employee e, string actorId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            MERGE dbo.Employee AS t USING (SELECT @EmployeeId AS EmployeeId) AS s ON t.EmployeeId = s.EmployeeId
            WHEN MATCHED THEN UPDATE SET Name = @Name, Email = @Email, GroupId = @GroupId, SectionCode = @SectionCode,
                 RankId = @RankId, RoleId = @RoleId, IsActive = @IsActive, UpdatedAt = SYSDATETIME(), UpdatedBy = @actorId
            WHEN NOT MATCHED THEN INSERT (EmployeeId, Name, Email, GroupId, SectionCode, RankId, RoleId, IsActive, CreatedBy)
                 VALUES (@EmployeeId, @Name, @Email, @GroupId, @SectionCode, @RankId, @RoleId, @IsActive, @actorId);
            """, new
        {
            e.EmployeeId,
            e.Name,
            e.Email,
            e.GroupId,
            SectionCode = string.IsNullOrEmpty(e.SectionCode) ? null : e.SectionCode,
            e.RankId,
            e.RoleId,
            e.IsActive,
            actorId,
        }, cancellationToken: ct));
    }

    internal static string EscapeLike(string? s) =>
        (s ?? "").Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");
}

public sealed class PermissionRepository(IDbConnectionFactory db) : IPermissionRepository
{
    public async Task<IReadOnlyList<Feature>> GetFeaturesAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<Feature>(new CommandDefinition(
            "SELECT FeatureId, ParentId, Name, Path, SortOrder FROM dbo.Feature ORDER BY SortOrder, FeatureId", cancellationToken: ct))).AsList();
    }

    public async Task<IReadOnlySet<int>> GetRoleFeaturesAsync(int roleId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        var ids = await conn.QueryAsync<int>(new CommandDefinition(
            "SELECT FeatureId FROM dbo.RoleFeature WHERE RoleId = @roleId", new { roleId }, cancellationToken: ct));
        return ids.ToHashSet();
    }

    public async Task SetRoleFeaturesAsync(int roleId, IEnumerable<int> featureIds, CancellationToken ct = default)
    {
        var ids = featureIds.Distinct().ToList();
        await using var conn = await db.OpenAsync(ct);
        await conn.InTransactionAsync(async tx =>
        {
            await conn.ExecuteAsync("DELETE dbo.RoleFeature WHERE RoleId = @roleId", new { roleId }, tx);
            if (ids.Count > 0)
                await conn.ExecuteAsync(
                    "INSERT dbo.RoleFeature (RoleId, FeatureId) SELECT @roleId, FeatureId FROM dbo.Feature WHERE FeatureId = @id",
                    ids.Select(id => new { roleId, id }), tx);
        });
    }
}

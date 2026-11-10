using System.Data;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;
using Taipower.Safety.Core.Security;

namespace Taipower.Safety.Infrastructure.Migration;

/// <summary>
/// One-off import of the 2020 TaipowerReport database into the new schema.
/// Run against an empty, freshly migrated target database. Ids of reference
/// tables are preserved so legacy foreign keys map 1:1.
/// </summary>
public sealed partial class LegacyImporter(string legacyConnectionString, string targetConnectionString, string? legacyFileRoot, string? targetFileRoot, TextWriter log)
{
    private readonly List<string> _warnings = new();

    public async Task RunAsync()
    {
        await using var src = new SqlConnection(legacyConnectionString);
        await using var dst = new SqlConnection(targetConnectionString);
        await src.OpenAsync();
        await dst.OpenAsync();

        if (await dst.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Employee") > 0)
            throw new InvalidOperationException("Target database already contains employees; import expects an empty, freshly migrated database.");

        await using var tx = (SqlTransaction)await dst.BeginTransactionAsync();
        try
        {
            await ImportOrgAsync(src, dst, tx);
            await ImportReferenceAsync(src, dst, tx);
            await ImportPermissionsAsync(src, dst, tx);
            // Pr1 case data import is implemented in Migration/LegacyPr1Importer.cs.
            await new LegacyPr1Importer(src, dst, tx, Warn, log).RunAsync();
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }

        foreach (var w in _warnings) log.WriteLine("WARN  " + w);
        log.WriteLine($"Import finished with {_warnings.Count} warning(s).");
    }

    private void Warn(string message) => _warnings.Add(message);

    private static string? Clean(string? s)
    {
        if (s is null) return null;
        // Legacy data contains stray '?' / control characters from encoding problems.
        var t = Regex.Replace(s, @"[\u0000-\u001F​-‏﻿]", "").Trim().TrimStart('?').Trim();
        return t.Length == 0 ? null : t;
    }

    private async Task ImportOrgAsync(SqlConnection src, SqlConnection dst, IDbTransaction tx)
    {
        // Groups (V002 seeds the known ones; add/refresh anything else)
        var groups = await src.QueryAsync<(string Id, string Name, string? Upper)>(
            "SELECT RTRIM(Group_id), Group_name, NULLIF(RTRIM(Group_upper), '') FROM dbo.ORG_Group");
        // Parents first so the self-referencing FK is satisfied.
        foreach (var g in groups.OrderBy(g => g.Upper is null ? 0 : g.Upper.Length < 3 ? 1 : 2))
            await dst.ExecuteAsync("""
                MERGE dbo.OrgGroup AS t USING (SELECT @Id AS GroupId) AS s ON t.GroupId = s.GroupId
                WHEN MATCHED THEN UPDATE SET Name = @Name, ParentGroupId = @Upper
                WHEN NOT MATCHED THEN INSERT (GroupId, Name, ParentGroupId) VALUES (@Id, @Name, @Upper);
                """, new { g.Id, Name = Clean(g.Name) ?? g.Id, g.Upper }, tx);

        var sections = (await src.QueryAsync<(string GroupId, string Code, string Name)>(
            "SELECT RTRIM(Section_Gro_id), RTRIM(Section_id), Section_name FROM dbo.ORG_Section WHERE Section_Gro_id IS NOT NULL AND Section_id IS NOT NULL"))
            .GroupBy(s => (s.GroupId, s.Code)).Select(g => g.First()).ToList();
        foreach (var s in sections)
            await dst.ExecuteAsync("INSERT dbo.OrgSection (GroupId, SectionCode, Name) VALUES (@GroupId, @Code, @Name)",
                new { s.GroupId, s.Code, Name = Clean(s.Name) ?? s.Code }, tx);
        log.WriteLine($"Sections: {sections.Count}");

        var validSections = sections.Select(s => (s.GroupId, s.Code)).ToHashSet();
        var people = (await src.QueryAsync("""
            SELECT RTRIM(PerInfo_id) AS Id, PerInfo_name AS Name, PerInfo_email AS Email,
                   NULLIF(RTRIM(PerInfo_Gro_Group_id), '') AS GroupId, NULLIF(RTRIM(PerInfo_Sec_Section_id), '') AS SectionCode,
                   PerInfo_Ran_Rank_idx AS RankId, PerInfo_Rol_Role_idx AS RoleId, PerInfo_createdate AS CreatedAt
            FROM dbo.ORG_PerInfo
            """)).ToList();
        foreach (var p in people)
        {
            string? section = p.SectionCode;
            if (section is not null && !validSections.Contains(((string)p.GroupId, section)))
            {
                Warn($"Employee {p.Id}: section {p.GroupId}/{section} not found, cleared.");
                section = null;
            }
            await dst.ExecuteAsync("""
                INSERT dbo.Employee (EmployeeId, Name, Email, GroupId, SectionCode, RankId, RoleId, IsActive, CreatedAt, CreatedBy)
                VALUES (@Id, @Name, @Email, @GroupId, @SectionCode, @RankId, @RoleId, 1, COALESCE(@CreatedAt, SYSDATETIME()), 'import')
                """, new
            {
                Id = (string)p.Id,
                Name = Clean((string?)p.Name) ?? (string)p.Id,
                Email = Clean((string?)p.Email),
                GroupId = (string?)p.GroupId,
                SectionCode = section,
                RankId = (int?)p.RankId,
                RoleId = (int?)p.RoleId,
                CreatedAt = (DateTime?)p.CreatedAt,
            }, tx);
        }
        log.WriteLine($"Employees: {people.Count}");

        var known = people.Select(p => (string)p.Id).ToHashSet();
        var members = (await src.QueryAsync<(string Account, string? Salt, string? Hash, string EmployeeId, bool? Active)>(
            "SELECT RTRIM(Member_account), Member_passwordsalt, Member_password, RTRIM(Member_Per_PerInfo_id), Member_status FROM dbo.Member")).ToList();
        var imported = 0;
        foreach (var m in members)
        {
            if (!known.Contains(m.EmployeeId)) { Warn($"Account {m.Account}: employee {m.EmployeeId} missing, skipped."); continue; }
            if (string.IsNullOrEmpty(m.Hash) || m.Salt is null) { Warn($"Account {m.Account}: no password, skipped."); continue; }
            await dst.ExecuteAsync("""
                IF NOT EXISTS (SELECT 1 FROM dbo.UserAccount WHERE EmployeeId = @EmployeeId)
                INSERT dbo.UserAccount (Account, EmployeeId, PasswordHash, IsActive) VALUES (@Account, @EmployeeId, @Hash, @Active)
                """, new { m.Account, m.EmployeeId, Hash = PasswordHasher.FromLegacy(m.Salt, m.Hash), Active = m.Active ?? true }, tx);
            imported++;
        }
        log.WriteLine($"Accounts: {imported}/{members.Count}");
    }

    private async Task ImportReferenceAsync(SqlConnection src, SqlConnection dst, IDbTransaction tx)
    {
        async Task Copy(string table, string identityTable, string selectSql, string insertSql)
        {
            var rows = (await src.QueryAsync(selectSql)).ToList();
            await dst.ExecuteAsync($"SET IDENTITY_INSERT dbo.{identityTable} ON", transaction: tx);
            foreach (var r in rows) await dst.ExecuteAsync(insertSql, (object)r, tx);
            await dst.ExecuteAsync($"SET IDENTITY_INSERT dbo.{identityTable} OFF", transaction: tx);
            log.WriteLine($"{table}: {rows.Count}");
        }

        await Copy("PenaltyVersion", "PenaltyVersion",
            "SELECT PenV_idx AS Id, PenV_version AS Name FROM dbo.PenaltyVersion",
            "INSERT dbo.PenaltyVersion (PenaltyVersionId, Name) VALUES (@Id, @Name)");
        await Copy("Contractor", "Contractor",
            "SELECT Con_idx AS Id, Con_name AS Name FROM dbo.Contractor",
            "INSERT dbo.Contractor (ContractorId, Name) VALUES (@Id, @Name)");
        await Copy("EngineeringProject", "EngineeringProject",
            "SELECT EnPro_idx AS Id, RTRIM(EnPro_number) AS Number, EnPro_name AS Name, RTRIM(EnPro_UnitPeriod) AS UnitPeriod, EnPro_penaltyversion_id AS Pv FROM dbo.EngineeringProject",
            "INSERT dbo.EngineeringProject (ProjectId, ProjectNumber, Name, UnitPeriod, PenaltyVersionId) VALUES (@Id, COALESCE(@Number, ''), COALESCE(@Name, ''), @UnitPeriod, @Pv)");
        await Copy("Hazard", "Hazard",
            "SELECT harm_idx AS Id, harm_name AS Name FROM dbo.Harm",
            "INSERT dbo.Hazard (HazardId, Name) VALUES (@Id, @Name)");
        await Copy("DeficiencyType", "DeficiencyType",
            "SELECT MT_idx AS Id, MT_Harm_idx AS HazardId, MT_name AS Name FROM dbo.MissingType WHERE MT_Harm_idx IS NOT NULL",
            "INSERT dbo.DeficiencyType (DeficiencyTypeId, HazardId, Name) VALUES (@Id, @HazardId, @Name)");
        await Copy("DeficiencyItem", "DeficiencyItem",
            "SELECT MI_idx AS Id, MI_MT_idx AS TypeId, MI_name AS Name FROM dbo.MissingItems WHERE MI_MT_idx IN (SELECT MT_idx FROM dbo.MissingType WHERE MT_Harm_idx IS NOT NULL)",
            "INSERT dbo.DeficiencyItem (DeficiencyItemId, DeficiencyTypeId, Name) VALUES (@Id, @TypeId, @Name)");

        var links = (await src.QueryAsync<(int ProjectId, int ContractorId)>(
            "SELECT DISTINCT EngP_idx, Con_idx FROM dbo.Con_EngPro WHERE EngP_idx IS NOT NULL AND Con_idx IS NOT NULL")).ToList();
        foreach (var l in links)
            await dst.ExecuteAsync("""
                IF EXISTS (SELECT 1 FROM dbo.EngineeringProject WHERE ProjectId = @ProjectId) AND EXISTS (SELECT 1 FROM dbo.Contractor WHERE ContractorId = @ContractorId)
                INSERT dbo.ProjectContractor (ProjectId, ContractorId) VALUES (@ProjectId, @ContractorId)
                """, new { l.ProjectId, l.ContractorId }, tx);
        log.WriteLine($"ProjectContractor: {links.Count}");

        var contacts = (await src.QueryAsync<(string? Name, string? Email, string? ConId)>(
            "SELECT ConPer_manager, ConPer_mail, RTRIM(ConPer_Con_idx) FROM dbo.ContractorPersonnel")).ToList();
        foreach (var c in contacts)
        {
            var email = Clean(c.Email);
            if (email is null || !int.TryParse(c.ConId, out var conId)) { Warn($"Contractor contact '{c.Name}' skipped (no email/contractor)."); continue; }
            await dst.ExecuteAsync("""
                IF EXISTS (SELECT 1 FROM dbo.Contractor WHERE ContractorId = @conId)
                INSERT dbo.ContractorContact (ContractorId, Name, Email) VALUES (@conId, @name, @email)
                """, new { conId, name = Clean(c.Name) ?? "", email }, tx);
        }
        log.WriteLine($"ContractorContact: {contacts.Count}");
    }

    private async Task ImportPermissionsAsync(SqlConnection src, SqlConnection dst, IDbTransaction tx)
    {
        // Only features that exist in the new system are carried over (Pr1 + admin).
        var grants = (await src.QueryAsync<(int RoleId, int FeatureId)>(
            "SELECT DISTINCT Role_id, Func_id FROM dbo.Features_Role WHERE Role_id IS NOT NULL AND Func_id IS NOT NULL")).ToList();
        var count = await dst.ExecuteAsync("""
            INSERT dbo.RoleFeature (RoleId, FeatureId)
            SELECT @RoleId, @FeatureId
            WHERE EXISTS (SELECT 1 FROM dbo.Feature WHERE FeatureId = @FeatureId)
              AND EXISTS (SELECT 1 FROM dbo.OrgRole WHERE RoleId = @RoleId)
              AND NOT EXISTS (SELECT 1 FROM dbo.RoleFeature WHERE RoleId = @RoleId AND FeatureId = @FeatureId)
            """, grants.Select(g => new { g.RoleId, g.FeatureId }), tx);
        log.WriteLine($"RoleFeature: {count} of {grants.Count} legacy grants mapped");
    }
}

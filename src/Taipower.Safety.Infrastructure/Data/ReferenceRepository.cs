using Dapper;
using Taipower.Safety.Core.Reference;

namespace Taipower.Safety.Infrastructure.Data;

public sealed class ReferenceRepository(IDbConnectionFactory db) : IReferenceRepository
{
    public async Task<IReadOnlyList<EngineeringProject>> GetProjectsAsync(bool activeOnly = true, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        var projects = (await conn.QueryAsync<EngineeringProject>(new CommandDefinition(
            "SELECT ProjectId, ProjectNumber, Name, UnitPeriod, PenaltyVersionId, IsActive FROM dbo.EngineeringProject WHERE (@activeOnly = 0 OR IsActive = 1) ORDER BY ProjectNumber",
            new { activeOnly }, cancellationToken: ct))).AsList();
        var links = await conn.QueryAsync<(int ProjectId, int ContractorId)>(new CommandDefinition(
            "SELECT ProjectId, ContractorId FROM dbo.ProjectContractor", cancellationToken: ct));
        var byProject = links.ToLookup(l => l.ProjectId, l => l.ContractorId);
        foreach (var p in projects) p.ContractorIds = byProject[p.ProjectId].ToList();
        return projects;
    }

    public async Task<EngineeringProject?> GetProjectAsync(int projectId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        var p = await conn.QuerySingleOrDefaultAsync<EngineeringProject>(new CommandDefinition(
            "SELECT ProjectId, ProjectNumber, Name, UnitPeriod, PenaltyVersionId, IsActive FROM dbo.EngineeringProject WHERE ProjectId = @projectId",
            new { projectId }, cancellationToken: ct));
        if (p is not null)
            p.ContractorIds = (await conn.QueryAsync<int>(new CommandDefinition(
                "SELECT ContractorId FROM dbo.ProjectContractor WHERE ProjectId = @projectId", new { projectId }, cancellationToken: ct))).AsList();
        return p;
    }

    public async Task<int> SaveProjectAsync(EngineeringProject p, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        var id = p.ProjectId;
        await conn.InTransactionAsync(async tx =>
        {
            if (id == 0)
                id = await conn.ExecuteScalarAsync<int>("""
                    INSERT dbo.EngineeringProject (ProjectNumber, Name, UnitPeriod, PenaltyVersionId, IsActive)
                    OUTPUT INSERTED.ProjectId VALUES (@ProjectNumber, @Name, @UnitPeriod, @PenaltyVersionId, @IsActive)
                    """, p, tx);
            else
                await conn.ExecuteAsync("""
                    UPDATE dbo.EngineeringProject SET ProjectNumber = @ProjectNumber, Name = @Name, UnitPeriod = @UnitPeriod,
                           PenaltyVersionId = @PenaltyVersionId, IsActive = @IsActive WHERE ProjectId = @ProjectId
                    """, p, tx);

            await conn.ExecuteAsync("DELETE dbo.ProjectContractor WHERE ProjectId = @id", new { id }, tx);
            foreach (var cid in p.ContractorIds.Distinct())
                await conn.ExecuteAsync("INSERT dbo.ProjectContractor (ProjectId, ContractorId) VALUES (@id, @cid)", new { id, cid }, tx);
        });
        return id;
    }

    public async Task<IReadOnlyList<Contractor>> GetContractorsAsync(int? projectId = null, bool activeOnly = true, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        var list = (await conn.QueryAsync<Contractor>(new CommandDefinition("""
            SELECT c.ContractorId, c.Name, c.IsActive FROM dbo.Contractor c
            WHERE (@activeOnly = 0 OR c.IsActive = 1)
              AND (@projectId IS NULL OR EXISTS (SELECT 1 FROM dbo.ProjectContractor pc WHERE pc.ContractorId = c.ContractorId AND pc.ProjectId = @projectId))
            ORDER BY c.Name
            """, new { projectId, activeOnly }, cancellationToken: ct))).AsList();
        var contacts = (await conn.QueryAsync<ContractorContact>(new CommandDefinition(
            "SELECT ContactId, ContractorId, Name, Email FROM dbo.ContractorContact", cancellationToken: ct))).ToLookup(c => c.ContractorId);
        foreach (var c in list) c.Contacts = contacts[c.ContractorId].ToList();
        return list;
    }

    public async Task<Contractor?> GetContractorAsync(int contractorId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        var c = await conn.QuerySingleOrDefaultAsync<Contractor>(new CommandDefinition(
            "SELECT ContractorId, Name, IsActive FROM dbo.Contractor WHERE ContractorId = @contractorId", new { contractorId }, cancellationToken: ct));
        if (c is not null)
            c.Contacts = (await conn.QueryAsync<ContractorContact>(new CommandDefinition(
                "SELECT ContactId, ContractorId, Name, Email FROM dbo.ContractorContact WHERE ContractorId = @contractorId ORDER BY ContactId",
                new { contractorId }, cancellationToken: ct))).AsList();
        return c;
    }

    public async Task<int> SaveContractorAsync(Contractor c, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        var id = c.ContractorId;
        await conn.InTransactionAsync(async tx =>
        {
            if (id == 0)
                id = await conn.ExecuteScalarAsync<int>(
                    "INSERT dbo.Contractor (Name, IsActive) OUTPUT INSERTED.ContractorId VALUES (@Name, @IsActive)", c, tx);
            else
                await conn.ExecuteAsync("UPDATE dbo.Contractor SET Name = @Name, IsActive = @IsActive WHERE ContractorId = @ContractorId", c, tx);

            await conn.ExecuteAsync("DELETE dbo.ContractorContact WHERE ContractorId = @id", new { id }, tx);
            foreach (var contact in c.Contacts.Where(x => !string.IsNullOrWhiteSpace(x.Email)))
                await conn.ExecuteAsync("INSERT dbo.ContractorContact (ContractorId, Name, Email) VALUES (@id, @Name, @Email)",
                    new { id, contact.Name, contact.Email }, tx);
        });
        return id;
    }

    public async Task<IReadOnlyList<Hazard>> GetHazardsAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<Hazard>(new CommandDefinition("SELECT HazardId, Name FROM dbo.Hazard ORDER BY HazardId", cancellationToken: ct))).AsList();
    }

    public async Task<IReadOnlyList<DeficiencyType>> GetDeficiencyTypesAsync(int? hazardId = null, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<DeficiencyType>(new CommandDefinition(
            "SELECT DeficiencyTypeId, HazardId, Name FROM dbo.DeficiencyType WHERE (@hazardId IS NULL OR HazardId = @hazardId) ORDER BY DeficiencyTypeId",
            new { hazardId }, cancellationToken: ct))).AsList();
    }

    public async Task<IReadOnlyList<DeficiencyItem>> GetDeficiencyItemsAsync(int? typeId = null, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<DeficiencyItem>(new CommandDefinition(
            "SELECT DeficiencyItemId, DeficiencyTypeId, Name FROM dbo.DeficiencyItem WHERE (@typeId IS NULL OR DeficiencyTypeId = @typeId) ORDER BY DeficiencyItemId",
            new { typeId }, cancellationToken: ct))).AsList();
    }

    public async Task<int> SaveHazardAsync(Hazard h, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        if (h.HazardId == 0)
            return await conn.ExecuteScalarAsync<int>(new CommandDefinition("INSERT dbo.Hazard (Name) OUTPUT INSERTED.HazardId VALUES (@Name)", h, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("UPDATE dbo.Hazard SET Name = @Name WHERE HazardId = @HazardId", h, cancellationToken: ct));
        return h.HazardId;
    }

    public async Task<int> SaveDeficiencyTypeAsync(DeficiencyType t, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        if (t.DeficiencyTypeId == 0)
            return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "INSERT dbo.DeficiencyType (HazardId, Name) OUTPUT INSERTED.DeficiencyTypeId VALUES (@HazardId, @Name)", t, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.DeficiencyType SET HazardId = @HazardId, Name = @Name WHERE DeficiencyTypeId = @DeficiencyTypeId", t, cancellationToken: ct));
        return t.DeficiencyTypeId;
    }

    public async Task<int> SaveDeficiencyItemAsync(DeficiencyItem i, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        if (i.DeficiencyItemId == 0)
            return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "INSERT dbo.DeficiencyItem (DeficiencyTypeId, Name) OUTPUT INSERTED.DeficiencyItemId VALUES (@DeficiencyTypeId, @Name)", i, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.DeficiencyItem SET DeficiencyTypeId = @DeficiencyTypeId, Name = @Name WHERE DeficiencyItemId = @DeficiencyItemId", i, cancellationToken: ct));
        return i.DeficiencyItemId;
    }

    public async Task<IReadOnlyList<PenaltyVersion>> GetPenaltyVersionsAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<PenaltyVersion>(new CommandDefinition(
            "SELECT PenaltyVersionId, Name FROM dbo.PenaltyVersion ORDER BY PenaltyVersionId", cancellationToken: ct))).AsList();
    }
}

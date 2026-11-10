using System.Text;
using Dapper;
using Taipower.Safety.Core.Forms.Pr5;

namespace Taipower.Safety.Infrastructure.Data;

public sealed class Pr5Repository(IDbConnectionFactory db) : IPr5Repository
{
    public async Task<string> NextNumberAsync(DateTime date, CancellationToken ct = default)
    {
        // P5-<民國yyyMMdd>-<2碼流水>
        var roc = $"{date.Year - 1911:D3}{date:MMdd}";
        var prefix = $"P5-{roc}-";
        await using var conn = await db.OpenAsync(ct);
        var next = await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT ISNULL(MAX(TRY_CAST(SUBSTRING(PenaltyNumber, LEN(@prefix) + 1, 2) AS int)), 0) + 1
            FROM dbo.Pr5Penalty WHERE PenaltyNumber LIKE @prefix + '[0-9][0-9]'
            """, new { prefix }, cancellationToken: ct));
        return $"{prefix}{next:D2}";
    }

    public async Task<long> CreateAsync(Pr5Penalty p, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT dbo.Pr5Penalty
                (PenaltyNumber, Source, SourceFormId, SourceItemId, ProjectId, ContractorId, AuditGroupId,
                 HazardId, DeficiencyTypeId, DeficiencyItemId, ViolationDate, Place, UnitPeriod,
                 PenaltyItemCode, PenaltyAmount, ImproveDeadline, Remark, Status, CreatedBy)
            OUTPUT INSERTED.PenaltyId
            VALUES
                (@PenaltyNumber, @Source, @SourceFormId, @SourceItemId, @ProjectId, @ContractorId, @AuditGroupId,
                 @HazardId, @DeficiencyTypeId, @DeficiencyItemId, @ViolationDate, @Place, @UnitPeriod,
                 @PenaltyItemCode, @PenaltyAmount, @ImproveDeadline, @Remark, @Status, @CreatedBy)
            """, new
        {
            p.PenaltyNumber,
            Source = (byte)p.Source,
            p.SourceFormId, p.SourceItemId, p.ProjectId, p.ContractorId, p.AuditGroupId,
            p.HazardId, p.DeficiencyTypeId, p.DeficiencyItemId, p.ViolationDate, p.Place, p.UnitPeriod,
            p.PenaltyItemCode, p.PenaltyAmount, p.ImproveDeadline, p.Remark,
            Status = (byte)p.Status, p.CreatedBy,
        }, cancellationToken: ct));
    }

    public async Task<Pr5Penalty?> GetAsync(long penaltyId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        var r = await conn.QuerySingleOrDefaultAsync(new CommandDefinition("""
            SELECT PenaltyId, PenaltyNumber, Source, SourceFormId, SourceItemId, ProjectId, ContractorId, AuditGroupId,
                   HazardId, DeficiencyTypeId, DeficiencyItemId, ViolationDate, Place, UnitPeriod,
                   PenaltyItemCode, PenaltyAmount, ImproveDeadline, Remark, Status, CreatedBy, CreatedAt, NotifiedAt, RowVer
            FROM dbo.Pr5Penalty WHERE PenaltyId = @penaltyId
            """, new { penaltyId }, cancellationToken: ct));
        if (r is null) return null;
        return new Pr5Penalty
        {
            PenaltyId = r.PenaltyId,
            PenaltyNumber = r.PenaltyNumber,
            Source = (Pr5Source)(byte)r.Source,
            SourceFormId = r.SourceFormId,
            SourceItemId = r.SourceItemId,
            ProjectId = r.ProjectId,
            ContractorId = r.ContractorId,
            AuditGroupId = r.AuditGroupId,
            HazardId = r.HazardId,
            DeficiencyTypeId = r.DeficiencyTypeId,
            DeficiencyItemId = r.DeficiencyItemId,
            ViolationDate = r.ViolationDate,
            Place = r.Place,
            UnitPeriod = r.UnitPeriod,
            PenaltyItemCode = r.PenaltyItemCode,
            PenaltyAmount = r.PenaltyAmount,
            ImproveDeadline = r.ImproveDeadline,
            Remark = r.Remark,
            Status = (Pr5Status)(byte)r.Status,
            CreatedBy = r.CreatedBy,
            CreatedAt = r.CreatedAt,
            NotifiedAt = r.NotifiedAt,
            RowVersion = r.RowVer,
        };
    }

    public async Task<IReadOnlyList<Pr5Row>> SearchAsync(Pr5SearchQuery q, CancellationToken ct = default)
    {
        var sql = new StringBuilder("""
            SELECT p.PenaltyId, p.PenaltyNumber, p.Source, p.Status, pr.Name AS ProjectName, c.Name AS ContractorName,
                   p.ViolationDate, p.PenaltyAmount, p.ImproveDeadline, p.PenaltyItemCode
            FROM dbo.Pr5Penalty p
            LEFT JOIN dbo.EngineeringProject pr ON pr.ProjectId = p.ProjectId
            LEFT JOIN dbo.Contractor c ON c.ContractorId = p.ContractorId
            WHERE 1 = 1
            """);
        if (q.ProjectId is not null) sql.Append(" AND p.ProjectId = @ProjectId");
        if (q.ContractorId is not null) sql.Append(" AND p.ContractorId = @ContractorId");
        if (q.Status is not null) sql.Append(" AND p.Status = @Status");
        if (q.From is not null) sql.Append(" AND p.ViolationDate >= @From");
        if (q.To is not null) sql.Append(" AND p.ViolationDate <= @To");
        sql.Append(" ORDER BY p.PenaltyId DESC");

        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync(new CommandDefinition(sql.ToString(),
            new { q.ProjectId, q.ContractorId, Status = (int?)q.Status, q.From, q.To }, cancellationToken: ct));
        return rows.Select(r => new Pr5Row(
            (long)r.PenaltyId, (string)r.PenaltyNumber, (Pr5Source)(byte)r.Source, (Pr5Status)(byte)r.Status,
            (string?)r.ProjectName ?? "", (string?)r.ContractorName ?? "", (DateTime?)r.ViolationDate,
            (decimal?)r.PenaltyAmount, (DateTime?)r.ImproveDeadline, (string?)r.PenaltyItemCode)).ToList();
    }

    public async Task<bool> SetStatusAsync(long penaltyId, Pr5Status status, byte[]? rowVersion, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        var affected = await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE dbo.Pr5Penalty SET Status = @Status WHERE PenaltyId = @penaltyId AND RowVer = @rowVersion
            """, new { penaltyId, Status = (byte)status, rowVersion }, cancellationToken: ct));
        return affected > 0;
    }

    public async Task LinkPr1ItemAsync(long itemId, string penaltyNumber, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.Pr1Item SET PenaltyTicketNo = @penaltyNumber WHERE ItemId = @itemId",
            new { itemId, penaltyNumber }, cancellationToken: ct));
    }

    public async Task<DateTime?> SetNotifiedAsync(long penaltyId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<DateTime?>(new CommandDefinition("""
            UPDATE dbo.Pr5Penalty SET NotifiedAt = SYSDATETIME() OUTPUT INSERTED.NotifiedAt WHERE PenaltyId = @penaltyId
            """, new { penaltyId }, cancellationToken: ct));
    }
}

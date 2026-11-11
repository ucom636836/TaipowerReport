using System.Text;
using Dapper;
using Taipower.Safety.Core.Forms.Pr4;
using Taipower.Safety.Core.Forms.Workflow;

namespace Taipower.Safety.Infrastructure.Data;

public sealed class Pr4Repository(IDbConnectionFactory db) : IPr4Repository
{
    public async Task<string> NextFormNumberAsync(DateTime date, CancellationToken ct = default)
    {
        // 單號 = SC-<民國yyyMMdd>-<2碼流水>-0（末碼為重會次數；前綴可經甲方同意調整）
        var roc = $"{date.Year - 1911:D3}{date:MMdd}";
        var prefix = $"SC-{roc}-";
        await using var conn = await db.OpenAsync(ct);
        var next = await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT ISNULL(MAX(TRY_CAST(SUBSTRING(FormNumber, LEN(@prefix) + 1, 2) AS int)), 0) + 1
            FROM dbo.FormInstance WHERE FormType = 'PR4' AND FormNumber LIKE @prefix + '[0-9][0-9]-%'
            """, new { prefix }, cancellationToken: ct));
        return $"{prefix}{next:D2}-0";
    }

    public async Task<long> CreateAsync(Pr4Form form, string actorTitle, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        long formId = 0;
        await conn.InTransactionAsync(async tx =>
        {
            formId = await conn.ExecuteScalarAsync<long>("""
                INSERT dbo.FormInstance (FormType, FormNumber, Revision, CurrentStep, Status, AssigneeId, CreatedBy, CreatedAt)
                OUTPUT INSERTED.FormId
                VALUES ('PR4', @FormNumber, 0, @Step, 'InProgress', @Assignee, @CreatedBy, SYSDATETIME())
                """, new { form.FormNumber, Step = form.Step.ToString(), Assignee = form.PendingRecipientId, form.CreatedBy }, tx);

            await conn.ExecuteAsync("""
                INSERT dbo.Pr4Form (FormId, SpotCheckDate, ProjectId, ContractorId, Location, ArriveTime, LeaveTime,
                    GroupId, ImproveDeadline, NeedImprove)
                VALUES (@formId, @SpotCheckDate, @ProjectId, @ContractorId, @Location, @ArriveTime, @LeaveTime,
                    @GroupId, @ImproveDeadline, @NeedImprove)
                """, new
            {
                formId, form.SpotCheckDate, form.ProjectId, form.ContractorId, form.Location, form.ArriveTime, form.LeaveTime,
                form.GroupId, form.ImproveDeadline, form.NeedImprove,
            }, tx);

            await InsertItemsAsync(conn, tx, formId, form.Items);

            await conn.ExecuteAsync("""
                INSERT dbo.WorkflowHistory (FormId, Revision, FromStep, ToStep, Action, ActorId, ActorName, ActorTitle, AssigneeId, Comment)
                VALUES (@formId, 0, NULL, @ToStep, '建單', @ActorId, NULL, @ActorTitle, @Assignee, NULL)
                """, new { formId, ToStep = form.Step.ToString(), ActorId = form.CreatedBy, actorTitle, Assignee = form.PendingRecipientId }, tx);
        });
        return formId;
    }

    private static async Task InsertItemsAsync(Microsoft.Data.SqlClient.SqlConnection conn, System.Data.IDbTransaction tx, long formId, IEnumerable<Pr4Item> items)
    {
        foreach (var it in items)
            await conn.ExecuteAsync("""
                INSERT dbo.Pr4Item (FormId, Kind, Ordinal, Description, HazardId, DeficiencyTypeId, DeficiencyItemId, HazardLabel)
                VALUES (@formId, @Kind, @Ordinal, @Description, @HazardId, @DeficiencyTypeId, @DeficiencyItemId, @HazardLabel)
                """, new { formId, Kind = (byte)it.Kind, it.Ordinal, it.Description, it.HazardId, it.DeficiencyTypeId, it.DeficiencyItemId, it.HazardLabel }, tx);
    }

    public async Task<Pr4Form?> GetAsync(long formId, CancellationToken ct = default) =>
        await LoadAsync("i.FormId = @key", new { key = formId }, ct);

    public async Task<Pr4Form?> GetByNumberAsync(string formNumber, CancellationToken ct = default) =>
        await LoadAsync("i.FormType = 'PR4' AND i.FormNumber = @key", new { key = formNumber }, ct);

    private async Task<Pr4Form?> LoadAsync(string where, object args, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync(new CommandDefinition($"""
            SELECT i.FormId, i.FormNumber, i.Revision, i.CurrentStep, i.Status, i.AssigneeId, i.CreatedBy, i.CreatedAt, i.RowVer,
                   f.SpotCheckDate, f.ProjectId, f.ContractorId, f.Location, f.ArriveTime, f.LeaveTime,
                   f.GroupId, f.ImproveDeadline, f.NeedImprove, f.IsJump,
                   f.InspectionChiefId, f.InspectionManagerId, f.HandlingOfficerId, f.HandlingChiefId, f.HandlingManagerId,
                   h.ActorId AS HolderId
            FROM dbo.FormInstance i
            JOIN dbo.Pr4Form f ON f.FormId = i.FormId
            OUTER APPLY (SELECT TOP 1 ActorId FROM dbo.WorkflowHistory w WHERE w.FormId = i.FormId ORDER BY w.HistoryId DESC) h
            WHERE {where}
            """, args, cancellationToken: ct));
        if (row is null) return null;

        var form = MapForm(row);
        form.Items = (await conn.QueryAsync<Pr4Item>(new CommandDefinition("""
            SELECT ItemId, FormId, Kind, Ordinal, Description, HazardId, DeficiencyTypeId, DeficiencyItemId, HazardLabel,
                   Improved, NotImprovedReason, ImprovedDate, Verified, VerifyReason, PenaltyTicketNo AS PenaltyTicketNumber
            FROM dbo.Pr4Item WHERE FormId = @id ORDER BY Kind, Ordinal
            """, new { id = form.FormId }, cancellationToken: ct))).AsList();
        return form;
    }

    private static Pr4Form MapForm(dynamic r) => new()
    {
        FormId = r.FormId,
        FormNumber = r.FormNumber,
        Retimes = r.Revision,
        Step = ParseStep((string)r.CurrentStep),
        Status = Enum.Parse<FormStatus>((string)r.Status),
        PendingRecipientId = r.AssigneeId,
        CurrentHolderId = r.HolderId,
        SpotCheckDate = r.SpotCheckDate,
        ProjectId = r.ProjectId,
        ContractorId = r.ContractorId,
        Location = r.Location,
        ArriveTime = r.ArriveTime,
        LeaveTime = r.LeaveTime,
        GroupId = r.GroupId,
        ImproveDeadline = r.ImproveDeadline,
        NeedImprove = r.NeedImprove,
        IsJump = r.IsJump,
        InspectionChiefId = r.InspectionChiefId,
        InspectionManagerId = r.InspectionManagerId,
        HandlingOfficerId = r.HandlingOfficerId,
        HandlingChiefId = r.HandlingChiefId,
        HandlingManagerId = r.HandlingManagerId,
        CreatedBy = r.CreatedBy,
        CreatedAt = r.CreatedAt,
        RowVersion = r.RowVer,
    };

    private static Pr4Step ParseStep(string s)
    {
        var parts = s.Split('/');
        return new Pr4Step(Enum.Parse<Pr4Stage>(parts[0]), Enum.Parse<Position>(parts[1]), Enum.Parse<Transit>(parts[2]));
    }

    public async Task ApplyTransitionAsync(Pr4Form form, Pr4Transition t, string actorId, string actorTitle, string? comment, CancellationToken ct = default)
    {
        var newRetimes = form.Retimes + (t.IncrementRetimes ? 1 : 0);
        await using var conn = await db.OpenAsync(ct);
        await conn.InTransactionAsync(async tx =>
        {
            var affected = await conn.ExecuteAsync("""
                UPDATE dbo.FormInstance
                   SET CurrentStep = @Step, Status = @Status, Revision = @Revision, AssigneeId = @Assignee, UpdatedAt = SYSDATETIME()
                 WHERE FormId = @FormId AND RowVer = @RowVer
                """, new
            {
                Step = t.To.ToString(),
                Status = t.Status.ToString(),
                Revision = newRetimes,
                Assignee = form.PendingRecipientId,
                form.FormId,
                RowVer = form.RowVersion,
            }, tx);
            if (affected == 0)
                throw new Pr4ConcurrencyException(form.FormNumber);

            await conn.ExecuteAsync("""
                UPDATE dbo.Pr4Form SET IsJump = @IsJump,
                    InspectionChiefId = @InspectionChiefId, InspectionManagerId = @InspectionManagerId,
                    HandlingOfficerId = @HandlingOfficerId, HandlingChiefId = @HandlingChiefId, HandlingManagerId = @HandlingManagerId
                 WHERE FormId = @FormId
                """, new
            {
                form.IsJump,
                form.InspectionChiefId, form.InspectionManagerId,
                form.HandlingOfficerId, form.HandlingChiefId, form.HandlingManagerId, form.FormId,
            }, tx);

            await conn.ExecuteAsync("""
                INSERT dbo.WorkflowHistory (FormId, Revision, FromStep, ToStep, Action, ActorId, ActorTitle, AssigneeId, Comment)
                VALUES (@FormId, @Revision, @FromStep, @ToStep, @Action, @ActorId, @ActorTitle, @Assignee, @Comment)
                """, new
            {
                form.FormId, Revision = newRetimes, FromStep = form.Step.ToString(), ToStep = t.To.ToString(),
                Action = t.HistoryLabel ?? "", ActorId = actorId, actorTitle, Assignee = form.PendingRecipientId, comment,
            }, tx);
        });
    }

    public async Task<IReadOnlyList<Pr4InboxRow>> GetInboxAsync(string employeeId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync(new CommandDefinition(InboxSelect + """
            WHERE i.Status = 'InProgress' AND i.IsLegacy = 0 AND i.AssigneeId = @employeeId
            ORDER BY i.UpdatedAt DESC, i.FormId DESC
            """, new { employeeId }, cancellationToken: ct));
        return rows.Select(r => (Pr4InboxRow)MapInbox(r, employeeId)).ToList();
    }

    public async Task<IReadOnlyList<Pr4InboxRow>> SearchAsync(Pr4SearchQuery q, string employeeId, bool allScopes, CancellationToken ct = default)
    {
        var sql = new StringBuilder(InboxSelect).Append(" WHERE i.FormType = 'PR4'");
        if (!allScopes)
            sql.Append(" AND (i.CreatedBy = @employeeId OR EXISTS (SELECT 1 FROM dbo.WorkflowHistory w WHERE w.FormId = i.FormId AND w.ActorId = @employeeId))");
        if (q.ProjectId is not null) sql.Append(" AND f.ProjectId = @ProjectId");
        if (q.ContractorId is not null) sql.Append(" AND f.ContractorId = @ContractorId");
        if (q.From is not null) sql.Append(" AND f.SpotCheckDate >= @From");
        if (q.To is not null) sql.Append(" AND f.SpotCheckDate <= @To");
        sql.Append(" ORDER BY f.SpotCheckDate DESC, i.FormId DESC");

        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync(new CommandDefinition(sql.ToString(),
            new { employeeId, q.ProjectId, q.ContractorId, q.From, q.To }, cancellationToken: ct));
        return rows.Select(r => (Pr4InboxRow)MapInbox(r, employeeId)).ToList();
    }

    private const string InboxSelect = """
        SELECT i.FormId, i.FormNumber, i.Revision, i.CurrentStep, i.Status, i.AssigneeId, i.CreatedBy,
               f.SpotCheckDate, p.Name AS ProjectName, c.Name AS ContractorName, ins.Name AS InspectorName,
               (SELECT TOP 1 ActorId FROM dbo.WorkflowHistory w WHERE w.FormId = i.FormId ORDER BY w.HistoryId DESC) AS HolderId
        FROM dbo.FormInstance i
        JOIN dbo.Pr4Form f ON f.FormId = i.FormId
        LEFT JOIN dbo.EngineeringProject p ON p.ProjectId = f.ProjectId
        LEFT JOIN dbo.Contractor c ON c.ContractorId = f.ContractorId
        LEFT JOIN dbo.Employee ins ON ins.EmployeeId = i.CreatedBy
        """;

    private static Pr4InboxRow MapInbox(dynamic r, string me)
    {
        var step = ParseStep((string)r.CurrentStep);
        string? holder = r.HolderId;
        string? recipient = r.AssigneeId;
        var inTransit = holder != recipient;
        var canWithdraw = inTransit && holder == me;
        var canOpen = recipient == me && (string)r.Status == "InProgress";
        return new Pr4InboxRow((long)r.FormId, (string)r.FormNumber, (int)r.Revision, step,
            Enum.Parse<FormStatus>((string)r.Status), (DateTime)r.SpotCheckDate,
            (string?)r.ProjectName ?? "", (string?)r.ContractorName ?? "", (string?)r.InspectorName ?? "",
            canWithdraw, canOpen);
    }

    public async Task<IReadOnlyList<WorkflowHistoryRow>> GetHistoryAsync(long formId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<WorkflowHistoryRow>(new CommandDefinition("""
            SELECT OccurredAt, ActorTitle, Action, FromStep, ToStep, Comment
            FROM dbo.WorkflowHistory WHERE FormId = @formId ORDER BY HistoryId
            """, new { formId }, cancellationToken: ct))).AsList();
    }
}

public sealed class Pr4ConcurrencyException(string formNumber)
    : Exception($"單號 {formNumber} 已被其他人變更，請重新整理後再操作。");

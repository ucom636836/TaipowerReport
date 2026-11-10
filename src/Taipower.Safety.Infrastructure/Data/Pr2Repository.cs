using System.Text;
using Dapper;
using Taipower.Safety.Core.Forms.Pr2;
using Taipower.Safety.Core.Forms.Workflow;

namespace Taipower.Safety.Infrastructure.Data;

public sealed class Pr2Repository(IDbConnectionFactory db) : IPr2Repository
{
    public async Task<string> NextFormNumberAsync(string checkType, DateTime date, CancellationToken ct = default)
    {
        // 單號 = <2碼類別>-<民國yyyMMdd>-<2碼流水>-0
        var roc = $"{date.Year - 1911:D3}{date:MMdd}";
        var prefix = $"{checkType}-{roc}-";
        await using var conn = await db.OpenAsync(ct);
        var next = await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT ISNULL(MAX(TRY_CAST(SUBSTRING(FormNumber, LEN(@prefix) + 1, 2) AS int)), 0) + 1
            FROM dbo.FormInstance WHERE FormType = 'PR2' AND FormNumber LIKE @prefix + '[0-9][0-9]-%'
            """, new { prefix }, cancellationToken: ct));
        return $"{prefix}{next:D2}-0";
    }

    public async Task<long> CreateAsync(Pr2Form form, string actorTitle, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        long formId = 0;
        await conn.InTransactionAsync(async tx =>
        {
            formId = await conn.ExecuteScalarAsync<long>("""
                INSERT dbo.FormInstance (FormType, FormNumber, Revision, CurrentStep, Status, AssigneeId, CreatedBy, CreatedAt)
                OUTPUT INSERTED.FormId
                VALUES ('PR2', @FormNumber, 0, @Step, 'InProgress', @Assignee, @CreatedBy, SYSDATETIME())
                """, new { form.FormNumber, Step = form.Step.ToString(), Assignee = form.PendingRecipientId, form.CreatedBy }, tx);

            await conn.ExecuteAsync("""
                INSERT dbo.Pr2Form (FormId, CheckType, CheckDate, ProjectId, ContractorId, LeaderId, TeamMembers, ImproveGroupId)
                VALUES (@formId, @CheckType, @CheckDate, @ProjectId, @ContractorId, @LeaderId, @TeamMembers, @ImproveGroupId)
                """, new { formId, form.CheckType, form.CheckDate, form.ProjectId, form.ContractorId, form.LeaderId, form.TeamMembers, form.ImproveGroupId }, tx);

            await InsertItemsAsync(conn, tx, formId, form.Items);

            await conn.ExecuteAsync("""
                INSERT dbo.WorkflowHistory (FormId, Revision, FromStep, ToStep, Action, ActorId, ActorName, ActorTitle, AssigneeId, Comment)
                VALUES (@formId, 0, NULL, @ToStep, '建單', @ActorId, NULL, @ActorTitle, @Assignee, NULL)
                """, new { formId, ToStep = form.Step.ToString(), ActorId = form.CreatedBy, actorTitle, Assignee = form.PendingRecipientId }, tx);
        });
        return formId;
    }

    private static async Task InsertItemsAsync(Microsoft.Data.SqlClient.SqlConnection conn, System.Data.IDbTransaction tx, long formId, IEnumerable<Pr2Item> items)
    {
        foreach (var it in items)
            await conn.ExecuteAsync("""
                INSERT dbo.Pr2Item (FormId, Kind, Ordinal, Description, HazardId, DeficiencyTypeId, DeficiencyItemId, HazardLabel)
                VALUES (@formId, @Kind, @Ordinal, @Description, @HazardId, @DeficiencyTypeId, @DeficiencyItemId, @HazardLabel)
                """, new { formId, Kind = (byte)it.Kind, it.Ordinal, it.Description, it.HazardId, it.DeficiencyTypeId, it.DeficiencyItemId, it.HazardLabel }, tx);
    }

    public async Task<Pr2Form?> GetAsync(long formId, CancellationToken ct = default) =>
        await LoadAsync("i.FormId = @key", new { key = formId }, ct);

    public async Task<Pr2Form?> GetByNumberAsync(string formNumber, CancellationToken ct = default) =>
        await LoadAsync("i.FormType = 'PR2' AND i.FormNumber = @key", new { key = formNumber }, ct);

    private async Task<Pr2Form?> LoadAsync(string where, object args, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync(new CommandDefinition($"""
            SELECT i.FormId, i.FormNumber, i.Revision, i.CurrentStep, i.Status, i.AssigneeId, i.CreatedBy, i.CreatedAt, i.RowVer,
                   f.CheckType, f.CheckDate, f.ProjectId, f.ContractorId, f.LeaderId, f.TeamMembers, f.ImproveGroupId,
                   f.InformGroupId, f.ImproveDeadline, f.ImproveCompletedDate, f.IsJump, f.IsProxy,
                   f.SafetyOfficerId, f.SafetyChiefId, f.SafetyManagerId, f.ImproveOfficerId, f.ImproveChiefId, f.ImproveManagerId,
                   f.VerifyOfficerId, f.VerifyChiefId, f.VerifyManagerId,
                   h.ActorId AS HolderId
            FROM dbo.FormInstance i
            JOIN dbo.Pr2Form f ON f.FormId = i.FormId
            OUTER APPLY (SELECT TOP 1 ActorId FROM dbo.WorkflowHistory w WHERE w.FormId = i.FormId ORDER BY w.HistoryId DESC) h
            WHERE {where}
            """, args, cancellationToken: ct));
        if (row is null) return null;

        var form = MapForm(row);
        form.Items = (await conn.QueryAsync<Pr2Item>(new CommandDefinition("""
            SELECT ItemId, FormId, Kind, Ordinal, Description, HazardId, DeficiencyTypeId, DeficiencyItemId, HazardLabel,
                   Improved, NotImprovedReason, ImprovedDate, Verified, VerifyReason, PenaltyTicketNo AS PenaltyTicketNumber
            FROM dbo.Pr2Item WHERE FormId = @id ORDER BY Kind, Ordinal
            """, new { id = form.FormId }, cancellationToken: ct))).AsList();
        return form;
    }

    private static Pr2Form MapForm(dynamic r)
    {
        return new Pr2Form
        {
            FormId = r.FormId,
            FormNumber = r.FormNumber,
            Revision = r.Revision,
            Step = ParseStep((string)r.CurrentStep),
            Status = Enum.Parse<FormStatus>((string)r.Status),
            PendingRecipientId = r.AssigneeId,
            CurrentHolderId = r.HolderId,
            CheckType = r.CheckType,
            CheckDate = r.CheckDate,
            ProjectId = r.ProjectId,
            ContractorId = r.ContractorId,
            LeaderId = r.LeaderId,
            TeamMembers = r.TeamMembers,
            ImproveGroupId = r.ImproveGroupId,
            InformGroupId = r.InformGroupId,
            ImproveDeadline = r.ImproveDeadline,
            ImproveCompletedDate = r.ImproveCompletedDate,
            IsJump = r.IsJump,
            IsProxy = r.IsProxy,
            SafetyOfficerId = r.SafetyOfficerId,
            SafetyChiefId = r.SafetyChiefId,
            SafetyManagerId = r.SafetyManagerId,
            ImproveOfficerId = r.ImproveOfficerId,
            ImproveChiefId = r.ImproveChiefId,
            ImproveManagerId = r.ImproveManagerId,
            VerifyOfficerId = r.VerifyOfficerId,
            VerifyChiefId = r.VerifyChiefId,
            VerifyManagerId = r.VerifyManagerId,
            CreatedBy = r.CreatedBy,
            CreatedAt = r.CreatedAt,
            RowVersion = r.RowVer,
        };
    }

    private static WorkflowStep ParseStep(string s)
    {
        var parts = s.Split('/');
        return new WorkflowStep(Enum.Parse<Stage>(parts[0]), Enum.Parse<Position>(parts[1]), Enum.Parse<Transit>(parts[2]));
    }

    public async Task ApplyTransitionAsync(Pr2Form form, WorkflowTransition t, string actorId, string actorTitle, string? comment, CancellationToken ct = default)
    {
        var newRevision = form.Revision + (t.IncrementRevision ? 1 : 0);
        await using var conn = await db.OpenAsync(ct);
        await conn.InTransactionAsync(async tx =>
        {
            // 樂觀並行控制：RowVer 不符代表單已被他人變更。
            var affected = await conn.ExecuteAsync("""
                UPDATE dbo.FormInstance
                   SET CurrentStep = @Step, Status = @Status, Revision = @Revision, AssigneeId = @Assignee, UpdatedAt = SYSDATETIME()
                 WHERE FormId = @FormId AND RowVer = @RowVer
                """, new
            {
                Step = t.To.ToString(),
                Status = t.Status.ToString(),
                Revision = newRevision,
                Assignee = form.PendingRecipientId,
                form.FormId,
                RowVer = form.RowVersion,
            }, tx);
            if (affected == 0)
                throw new Pr2ConcurrencyException(form.FormNumber);

            await conn.ExecuteAsync("""
                UPDATE dbo.Pr2Form SET InformGroupId = @InformGroupId, ImproveDeadline = @ImproveDeadline,
                    ImproveCompletedDate = @ImproveCompletedDate, IsJump = @IsJump, IsProxy = @IsProxy,
                    SafetyOfficerId = @SafetyOfficerId, SafetyChiefId = @SafetyChiefId, SafetyManagerId = @SafetyManagerId,
                    ImproveOfficerId = @ImproveOfficerId, ImproveChiefId = @ImproveChiefId, ImproveManagerId = @ImproveManagerId,
                    VerifyOfficerId = @VerifyOfficerId, VerifyChiefId = @VerifyChiefId, VerifyManagerId = @VerifyManagerId
                 WHERE FormId = @FormId
                """, new
            {
                form.InformGroupId, form.ImproveDeadline, form.ImproveCompletedDate, form.IsJump, form.IsProxy,
                form.SafetyOfficerId, form.SafetyChiefId, form.SafetyManagerId,
                form.ImproveOfficerId, form.ImproveChiefId, form.ImproveManagerId,
                form.VerifyOfficerId, form.VerifyChiefId, form.VerifyManagerId, form.FormId,
            }, tx);

            await conn.ExecuteAsync("""
                INSERT dbo.WorkflowHistory (FormId, Revision, FromStep, ToStep, Action, ActorId, ActorTitle, AssigneeId, Comment)
                VALUES (@FormId, @Revision, @FromStep, @ToStep, @Action, @ActorId, @ActorTitle, @Assignee, @Comment)
                """, new
            {
                form.FormId, Revision = newRevision, FromStep = form.Step.ToString(), ToStep = t.To.ToString(),
                Action = t.HistoryLabel ?? "", ActorId = actorId, actorTitle, Assignee = form.PendingRecipientId, comment,
            }, tx);
        });
    }

    public async Task<IReadOnlyList<Pr2InboxRow>> GetInboxAsync(string employeeId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync(new CommandDefinition(InboxSelect + """
            WHERE i.Status = 'InProgress' AND i.IsLegacy = 0 AND i.AssigneeId = @employeeId
            ORDER BY i.UpdatedAt DESC, i.FormId DESC
            """, new { employeeId }, cancellationToken: ct));
        return rows.Select(r => (Pr2InboxRow)MapInbox(r, employeeId)).ToList();
    }

    public async Task<IReadOnlyList<Pr2InboxRow>> SearchAsync(Pr2SearchQuery q, string employeeId, bool allScopes, CancellationToken ct = default)
    {
        var sql = new StringBuilder(InboxSelect).Append(" WHERE i.FormType = 'PR2'");
        if (!allScopes)
            sql.Append(" AND (i.CreatedBy = @employeeId OR f.LeaderId = @employeeId OR EXISTS (SELECT 1 FROM dbo.WorkflowHistory w WHERE w.FormId = i.FormId AND w.ActorId = @employeeId))");
        if (q.ProjectId is not null) sql.Append(" AND f.ProjectId = @ProjectId");
        if (q.ContractorId is not null) sql.Append(" AND f.ContractorId = @ContractorId");
        if (!string.IsNullOrEmpty(q.LeaderId)) sql.Append(" AND f.LeaderId = @LeaderId");
        if (q.From is not null) sql.Append(" AND f.CheckDate >= @From");
        if (q.To is not null) sql.Append(" AND f.CheckDate <= @To");
        sql.Append(" ORDER BY f.CheckDate DESC, i.FormId DESC");

        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync(new CommandDefinition(sql.ToString(),
            new { employeeId, q.ProjectId, q.ContractorId, q.LeaderId, q.From, q.To }, cancellationToken: ct));
        return rows.Select(r => (Pr2InboxRow)MapInbox(r, employeeId)).ToList();
    }

    private const string InboxSelect = """
        SELECT i.FormId, i.FormNumber, i.Revision, i.CurrentStep, i.Status, i.AssigneeId, i.CreatedBy,
               f.CheckDate, p.Name AS ProjectName, c.Name AS ContractorName, le.Name AS LeaderName,
               (SELECT TOP 1 ActorId FROM dbo.WorkflowHistory w WHERE w.FormId = i.FormId ORDER BY w.HistoryId DESC) AS HolderId
        FROM dbo.FormInstance i
        JOIN dbo.Pr2Form f ON f.FormId = i.FormId
        LEFT JOIN dbo.EngineeringProject p ON p.ProjectId = f.ProjectId
        LEFT JOIN dbo.Contractor c ON c.ContractorId = f.ContractorId
        LEFT JOIN dbo.Employee le ON le.EmployeeId = f.LeaderId
        """;

    private static Pr2InboxRow MapInbox(dynamic r, string me)
    {
        var step = ParseStep((string)r.CurrentStep);
        string? holder = r.HolderId;
        string? recipient = r.AssigneeId;
        var inTransit = holder != recipient;
        var canWithdraw = inTransit && holder == me;
        var canOpen = recipient == me && (string)r.Status == "InProgress";
        return new Pr2InboxRow((long)r.FormId, (string)r.FormNumber, (int)r.Revision, step,
            Enum.Parse<FormStatus>((string)r.Status), (DateTime)r.CheckDate,
            (string?)r.ProjectName ?? "", (string?)r.ContractorName ?? "", (string?)r.LeaderName ?? "",
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

public sealed class Pr2ConcurrencyException(string formNumber)
    : Exception($"單號 {formNumber} 已被其他人變更，請重新整理後再操作。");

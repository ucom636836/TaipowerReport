using System.Text;
using Dapper;
using Taipower.Safety.Core.Forms.Pr1;

using Taipower.Safety.Core.Forms.Workflow;
namespace Taipower.Safety.Infrastructure.Data;

public sealed class Pr1Repository(IDbConnectionFactory db) : IPr1Repository
{
    public async Task<string> NextFormNumberAsync(string patrolType, DateTime date, CancellationToken ct = default)
    {
        // 單號 = <2碼類別>-<民國yyyMMdd>-<2碼流水>-0
        var roc = $"{date.Year - 1911:D3}{date:MMdd}";
        var prefix = $"{patrolType}-{roc}-";
        await using var conn = await db.OpenAsync(ct);
        // 以當日同類別最大流水 +1；UNIQUE 約束在併發下仍會擋重複，呼叫端重試即可。
        var next = await conn.ExecuteScalarAsync<int>(new CommandDefinition("""
            SELECT ISNULL(MAX(TRY_CAST(SUBSTRING(FormNumber, LEN(@prefix) + 1, 2) AS int)), 0) + 1
            FROM dbo.FormInstance WHERE FormType = 'PR1' AND FormNumber LIKE @prefix + '[0-9][0-9]-%'
            """, new { prefix }, cancellationToken: ct));
        return $"{prefix}{next:D2}-0";
    }

    public async Task<long> CreateAsync(Pr1Form form, string actorTitle, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        long formId = 0;
        await conn.InTransactionAsync(async tx =>
        {
            formId = await conn.ExecuteScalarAsync<long>("""
                INSERT dbo.FormInstance (FormType, FormNumber, Revision, CurrentStep, Status, AssigneeId, CreatedBy, CreatedAt)
                OUTPUT INSERTED.FormId
                VALUES ('PR1', @FormNumber, 0, @Step, 'InProgress', @Assignee, @CreatedBy, SYSDATETIME())
                """, new { form.FormNumber, Step = form.Step.ToString(), Assignee = form.PendingRecipientId, form.CreatedBy }, tx);

            await conn.ExecuteAsync("""
                INSERT dbo.Pr1Form (FormId, PatrolType, PatrolDate, ProjectId, ContractorId, LeaderId, TeamMembers, ImproveGroupId)
                VALUES (@formId, @PatrolType, @PatrolDate, @ProjectId, @ContractorId, @LeaderId, @TeamMembers, @ImproveGroupId)
                """, new { formId, form.PatrolType, form.PatrolDate, form.ProjectId, form.ContractorId, form.LeaderId, form.TeamMembers, form.ImproveGroupId }, tx);

            await InsertItemsAsync(conn, tx, formId, form.Items);

            await conn.ExecuteAsync("""
                INSERT dbo.WorkflowHistory (FormId, Revision, FromStep, ToStep, Action, ActorId, ActorName, ActorTitle, AssigneeId, Comment)
                VALUES (@formId, 0, NULL, @ToStep, '建單', @ActorId, NULL, @ActorTitle, @Assignee, NULL)
                """, new { formId, ToStep = form.Step.ToString(), ActorId = form.CreatedBy, actorTitle, Assignee = form.PendingRecipientId }, tx);
        });
        return formId;
    }

    private static async Task InsertItemsAsync(Microsoft.Data.SqlClient.SqlConnection conn, System.Data.IDbTransaction tx, long formId, IEnumerable<Pr1Item> items)
    {
        foreach (var it in items)
            await conn.ExecuteAsync("""
                INSERT dbo.Pr1Item (FormId, Kind, Ordinal, Description, HazardId, DeficiencyTypeId, DeficiencyItemId, HazardLabel)
                VALUES (@formId, @Kind, @Ordinal, @Description, @HazardId, @DeficiencyTypeId, @DeficiencyItemId, @HazardLabel)
                """, new { formId, Kind = (byte)it.Kind, it.Ordinal, it.Description, it.HazardId, it.DeficiencyTypeId, it.DeficiencyItemId, it.HazardLabel }, tx);
    }

    public async Task<Pr1Form?> GetAsync(long formId, CancellationToken ct = default) =>
        await LoadAsync("i.FormId = @key", new { key = formId }, ct);

    public async Task<Pr1Form?> GetByNumberAsync(string formNumber, CancellationToken ct = default) =>
        await LoadAsync("i.FormType = 'PR1' AND i.FormNumber = @key", new { key = formNumber }, ct);

    private async Task<Pr1Form?> LoadAsync(string where, object args, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync(new CommandDefinition($"""
            SELECT i.FormId, i.FormNumber, i.Revision, i.CurrentStep, i.Status, i.AssigneeId, i.CreatedBy, i.CreatedAt, i.RowVer,
                   f.PatrolType, f.PatrolDate, f.ProjectId, f.ContractorId, f.LeaderId, f.TeamMembers, f.ImproveGroupId,
                   f.InformGroupId, f.ImproveDeadline, f.ImproveCompletedDate, f.IsJump, f.IsProxy,
                   f.SafetyOfficerId, f.SafetyChiefId, f.SafetyManagerId, f.ImproveOfficerId, f.ImproveChiefId, f.ImproveManagerId,
                   f.VerifyOfficerId, f.VerifyChiefId, f.VerifyManagerId,
                   h.ActorId AS HolderId
            FROM dbo.FormInstance i
            JOIN dbo.Pr1Form f ON f.FormId = i.FormId
            OUTER APPLY (SELECT TOP 1 ActorId FROM dbo.WorkflowHistory w WHERE w.FormId = i.FormId ORDER BY w.HistoryId DESC) h
            WHERE {where}
            """, args, cancellationToken: ct));
        if (row is null) return null;

        var form = MapForm(row);
        form.Items = (await conn.QueryAsync<Pr1Item>(new CommandDefinition("""
            SELECT ItemId, FormId, Kind, Ordinal, Description, HazardId, DeficiencyTypeId, DeficiencyItemId, HazardLabel,
                   Improved, NotImprovedReason, ImprovedDate, Verified, VerifyReason, PenaltyTicketNo AS PenaltyTicketNumber
            FROM dbo.Pr1Item WHERE FormId = @id ORDER BY Kind, Ordinal
            """, new { id = form.FormId }, cancellationToken: ct))).AsList();

        // 逐項照片（缺失現況 / 改善後），依項目分派。
        List<Attachment> photos = (await conn.QueryAsync<Attachment>(new CommandDefinition("""
            SELECT a.AttachmentId, a.ItemId, a.Category, a.OriginalName, a.StoredName, a.ContentType, a.SizeBytes, a.UploadedBy, a.UploadedAt
            FROM dbo.Pr1Attachment a JOIN dbo.Pr1Item it ON it.ItemId = a.ItemId
            WHERE it.FormId = @id ORDER BY a.AttachmentId
            """, new { id = form.FormId }, cancellationToken: ct))).AsList();
        foreach (var it in form.Items)
        {
            long itemId = it.ItemId;
            it.BeforePhotos = photos.Where(a => a.ItemId == itemId && !a.IsAfter).ToList();
            it.AfterPhotos = photos.Where(a => a.ItemId == itemId && a.IsAfter).ToList();
        }
        return form;
    }

    public async Task<long> AddAttachmentAsync(Attachment a, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT dbo.Pr1Attachment (ItemId, Category, OriginalName, StoredName, ContentType, SizeBytes, UploadedBy)
            OUTPUT INSERTED.AttachmentId
            VALUES (@ItemId, @Category, @OriginalName, @StoredName, @ContentType, @SizeBytes, @UploadedBy)
            """, new { a.ItemId, a.Category, a.OriginalName, a.StoredName, a.ContentType, a.SizeBytes, a.UploadedBy }, cancellationToken: ct));
    }

    public async Task<Pr1AttachmentInfo?> GetAttachmentAsync(long attachmentId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Pr1AttachmentInfo>(new CommandDefinition("""
            SELECT a.AttachmentId, a.ItemId, it.FormId, a.Category, a.OriginalName, a.StoredName, a.ContentType
            FROM dbo.Pr1Attachment a JOIN dbo.Pr1Item it ON it.ItemId = a.ItemId
            WHERE a.AttachmentId = @attachmentId
            """, new { attachmentId }, cancellationToken: ct));
    }

    public async Task DeleteAttachmentAsync(long attachmentId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE dbo.Pr1Attachment WHERE AttachmentId = @attachmentId", new { attachmentId }, cancellationToken: ct));
    }

    public async Task UpdateItemResultsAsync(long formId, Stage stage, IReadOnlyList<Pr1ItemResult> results, CancellationToken ct = default)
    {
        // 依階段只更新對應欄位：C4 改善 / C5 查證。FormId 一併比對，避免跨單竄改。
        var sql = stage == Stage.Improvement
            ? """
              UPDATE dbo.Pr1Item SET Improved = @Improved, NotImprovedReason = @NotImprovedReason, ImprovedDate = @ImprovedDate
              WHERE ItemId = @ItemId AND FormId = @formId AND Kind = 1
              """
            : """
              UPDATE dbo.Pr1Item SET Verified = @Verified, VerifyReason = @VerifyReason
              WHERE ItemId = @ItemId AND FormId = @formId AND Kind = 1
              """;
        await using var conn = await db.OpenAsync(ct);
        await conn.InTransactionAsync(async tx =>
        {
            foreach (var r in results)
                await conn.ExecuteAsync(sql, new
                {
                    formId, r.ItemId,
                    r.Improved, r.NotImprovedReason, r.ImprovedDate,
                    r.Verified, r.VerifyReason,
                }, tx);
        });
    }

    private static Pr1Form MapForm(dynamic r)
    {
        var form = new Pr1Form
        {
            FormId = r.FormId,
            FormNumber = r.FormNumber,
            Revision = r.Revision,
            Step = ParseStep((string)r.CurrentStep),
            Status = Enum.Parse<FormStatus>((string)r.Status),
            PendingRecipientId = r.AssigneeId,
            CurrentHolderId = r.HolderId,
            PatrolType = r.PatrolType,
            PatrolDate = r.PatrolDate,
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
        return form;
    }

    private static WorkflowStep ParseStep(string s)
    {
        var parts = s.Split('/');
        return new WorkflowStep(Enum.Parse<Stage>(parts[0]), Enum.Parse<Position>(parts[1]), Enum.Parse<Transit>(parts[2]));
    }

    public async Task ApplyTransitionAsync(Pr1Form form, WorkflowTransition t, string actorId, string actorTitle, string? comment, CancellationToken ct = default)
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
                throw new Pr1ConcurrencyException(form.FormNumber);

            // 階段旗標 / 已簽核人 / 督促組別 / 期限等隨本次動作更新。
            await conn.ExecuteAsync("""
                UPDATE dbo.Pr1Form SET InformGroupId = @InformGroupId, ImproveDeadline = @ImproveDeadline,
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

    public async Task<IReadOnlyList<Pr1InboxRow>> GetInboxAsync(string employeeId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync(new CommandDefinition(InboxSelect + """
            WHERE i.Status = 'InProgress' AND i.IsLegacy = 0 AND i.AssigneeId = @employeeId
            ORDER BY i.UpdatedAt DESC, i.FormId DESC
            """, new { employeeId }, cancellationToken: ct));
        return rows.Select(r => (Pr1InboxRow)MapInbox(r, employeeId)).ToList();
    }

    public async Task<IReadOnlyList<Pr1InboxRow>> SearchAsync(Pr1SearchQuery q, string employeeId, bool allScopes, CancellationToken ct = default)
    {
        var sql = new StringBuilder(InboxSelect).Append(" WHERE i.FormType = 'PR1'");
        if (!allScopes)
            sql.Append(" AND (i.CreatedBy = @employeeId OR f.LeaderId = @employeeId OR EXISTS (SELECT 1 FROM dbo.WorkflowHistory w WHERE w.FormId = i.FormId AND w.ActorId = @employeeId))");
        if (q.ProjectId is not null) sql.Append(" AND f.ProjectId = @ProjectId");
        if (q.ContractorId is not null) sql.Append(" AND f.ContractorId = @ContractorId");
        if (!string.IsNullOrEmpty(q.LeaderId)) sql.Append(" AND f.LeaderId = @LeaderId");
        if (q.From is not null) sql.Append(" AND f.PatrolDate >= @From");
        if (q.To is not null) sql.Append(" AND f.PatrolDate <= @To");
        sql.Append(" ORDER BY f.PatrolDate DESC, i.FormId DESC");

        await using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync(new CommandDefinition(sql.ToString(),
            new { employeeId, q.ProjectId, q.ContractorId, q.LeaderId, q.From, q.To }, cancellationToken: ct));
        return rows.Select(r => (Pr1InboxRow)MapInbox(r, employeeId)).ToList();
    }

    private const string InboxSelect = """
        SELECT i.FormId, i.FormNumber, i.Revision, i.CurrentStep, i.Status, i.AssigneeId, i.CreatedBy,
               f.PatrolDate, p.Name AS ProjectName, c.Name AS ContractorName, le.Name AS LeaderName,
               (SELECT TOP 1 ActorId FROM dbo.WorkflowHistory w WHERE w.FormId = i.FormId ORDER BY w.HistoryId DESC) AS HolderId
        FROM dbo.FormInstance i
        JOIN dbo.Pr1Form f ON f.FormId = i.FormId
        LEFT JOIN dbo.EngineeringProject p ON p.ProjectId = f.ProjectId
        LEFT JOIN dbo.Contractor c ON c.ContractorId = f.ContractorId
        LEFT JOIN dbo.Employee le ON le.EmployeeId = f.LeaderId
        """;

    private static Pr1InboxRow MapInbox(dynamic r, string me)
    {
        var step = ParseStep((string)r.CurrentStep);
        string? holder = r.HolderId;
        string? recipient = r.AssigneeId;
        var inTransit = holder != recipient;
        var canWithdraw = inTransit && holder == me;              // 我送出、對方未承接
        var canOpen = recipient == me && (string)r.Status == "InProgress";
        return new Pr1InboxRow((long)r.FormId, (string)r.FormNumber, (int)r.Revision, step,
            Enum.Parse<FormStatus>((string)r.Status), (DateTime)r.PatrolDate,
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

public sealed class Pr1ConcurrencyException(string formNumber)
    : Exception($"單號 {formNumber} 已被其他人變更，請重新整理後再操作。");

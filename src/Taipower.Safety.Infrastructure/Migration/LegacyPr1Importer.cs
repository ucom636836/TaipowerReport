using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Taipower.Safety.Infrastructure.Migration;

/// <summary>
/// Imports legacy Pr1 cases + Improve items as read-only historical records
/// (FormInstance.IsLegacy = 1). The new workflow engine never routes these; they
/// exist for query/audit continuity. The legacy step code is preserved in history.
/// </summary>
internal sealed class LegacyPr1Importer(
    SqlConnection src, SqlConnection dst, IDbTransaction tx, Action<string> warn, TextWriter log)
{
    public async Task RunAsync()
    {
        var forms = (await src.QueryAsync("""
            SELECT RTRIM(Pr1_number) AS Number, RTRIM(Pr1_step) AS Step, RTRIM(Pr1_count) AS Cnt,
                   LEFT(RTRIM(Pr1_type), 2) AS PatrolType, Pr1_PrDate AS PatrolDate,
                   TRY_CAST(Pr1_EnPro_idx AS int) AS ProjectId, TRY_CAST(Pr1_Con_idx AS int) AS ContractorId,
                   RTRIM(Pr1_leader) AS LeaderId, RTRIM(Pr1_ImproveGroup) AS ImproveGroupId,
                   RTRIM(Pr1_C3_informgroup_id) AS InformGroupId, Pr1_C3_Impdate AS ImproveDeadline,
                   Pr1_C4_ImpdateOK AS ImproveCompletedDate, RTRIM(Pr1_createman) AS CreatedBy, Pr1_createdate AS CreatedAt,
                   RTRIM(Pr1_C3_audit_id1) AS SafetyOfficerId, RTRIM(Pr1_C3_audit_id2) AS SafetyChiefId, RTRIM(Pr1_C3_audit_id3) AS SafetyManagerId,
                   RTRIM(Pr1_C4_audit_id1) AS ImproveOfficerId, RTRIM(Pr1_C4_audit_id2) AS ImproveChiefId, RTRIM(Pr1_C4_audit_id3) AS ImproveManagerId,
                   RTRIM(Pr1_C5_audit_id1) AS VerifyOfficerId, RTRIM(Pr1_C5_audit_id2) AS VerifyChiefId, RTRIM(Pr1_C5_audit_id3) AS VerifyManagerId,
                   Pr1_All_OpinionStr AS History
            FROM dbo.Pr1 WHERE Pr1_number IS NOT NULL
            """)).ToList();

        var projectIds = (await dst.QueryAsync<int>("SELECT ProjectId FROM dbo.EngineeringProject", transaction: tx)).ToHashSet();
        var contractorIds = (await dst.QueryAsync<int>("SELECT ContractorId FROM dbo.Contractor", transaction: tx)).ToHashSet();
        var employeeIds = (await dst.QueryAsync<string>("SELECT EmployeeId FROM dbo.Employee", transaction: tx)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        int imported = 0, skipped = 0;
        foreach (var f in forms)
        {
            string number = f.Number;
            int? projectId = f.ProjectId;
            int? contractorId = f.ContractorId;
            string? leaderId = LegacyImporter_CleanId(f.LeaderId, employeeIds);
            string createdBy = LegacyImporter_CleanId(f.CreatedBy, employeeIds) ?? "import";

            if (projectId is null || !projectIds.Contains(projectId.Value) ||
                contractorId is null || !contractorIds.Contains(contractorId.Value))
            {
                warn($"Pr1 {number}: project/contractor missing, skipped.");
                skipped++;
                continue;
            }

            var status = ((string)f.Step) switch { "C699" => "Closed", "C698" => "Voided", _ => "InProgress" };
            var revision = int.TryParse((string?)f.Cnt, out var c) ? c : 0;

            var formId = await dst.ExecuteScalarAsync<long>("""
                INSERT dbo.FormInstance (FormType, FormNumber, Revision, CurrentStep, Status, AssigneeId, CreatedBy, CreatedAt, IsLegacy)
                OUTPUT INSERTED.FormId
                VALUES ('PR1', @number, @revision, @step, @status, NULL, @createdBy, COALESCE(@createdAt, SYSDATETIME()), 1)
                """, new { number, revision, step = (string)f.Step, status, createdBy, createdAt = (DateTime?)f.CreatedAt }, tx);

            await dst.ExecuteAsync("""
                INSERT dbo.Pr1Form (FormId, PatrolType, PatrolDate, ProjectId, ContractorId, LeaderId, ImproveGroupId,
                    InformGroupId, ImproveDeadline, ImproveCompletedDate, SafetyOfficerId, SafetyChiefId, SafetyManagerId,
                    ImproveOfficerId, ImproveChiefId, ImproveManagerId, VerifyOfficerId, VerifyChiefId, VerifyManagerId)
                VALUES (@formId, @patrolType, @patrolDate, @projectId, @contractorId, @leaderId, @improveGroupId,
                    @informGroupId, @improveDeadline, @improveCompletedDate, @so, @sc, @sm, @io, @ic, @im, @vo, @vc, @vm)
                """, new
            {
                formId,
                patrolType = string.IsNullOrWhiteSpace((string?)f.PatrolType) ? "SI" : (string)f.PatrolType,
                patrolDate = (DateTime?)f.PatrolDate ?? (DateTime?)f.CreatedAt ?? DateTime.Now,
                projectId,
                contractorId,
                leaderId = leaderId ?? createdBy,
                improveGroupId = NullIfEmpty((string?)f.ImproveGroupId),
                informGroupId = NullIfEmpty((string?)f.InformGroupId),
                improveDeadline = (DateTime?)f.ImproveDeadline,
                improveCompletedDate = (DateTime?)f.ImproveCompletedDate,
                so = LegacyImporter_CleanId((string?)f.SafetyOfficerId, employeeIds),
                sc = LegacyImporter_CleanId((string?)f.SafetyChiefId, employeeIds),
                sm = LegacyImporter_CleanId((string?)f.SafetyManagerId, employeeIds),
                io = LegacyImporter_CleanId((string?)f.ImproveOfficerId, employeeIds),
                ic = LegacyImporter_CleanId((string?)f.ImproveChiefId, employeeIds),
                im = LegacyImporter_CleanId((string?)f.ImproveManagerId, employeeIds),
                vo = LegacyImporter_CleanId((string?)f.VerifyOfficerId, employeeIds),
                vc = LegacyImporter_CleanId((string?)f.VerifyChiefId, employeeIds),
                vm = LegacyImporter_CleanId((string?)f.VerifyManagerId, employeeIds),
            }, tx);

            await ImportItemsAsync(number, formId);
            if (!string.IsNullOrWhiteSpace((string?)f.History))
                await dst.ExecuteAsync("""
                    INSERT dbo.Pr1StageComment (FormId, Stage, ActorId, Comment) VALUES (@formId, 0, NULL, @history)
                    """, new { formId, history = Truncate((string)f.History, 1000) }, tx);
            imported++;
        }
        log.WriteLine($"Pr1 forms: {imported} imported, {skipped} skipped (read-only history).");
    }

    private async Task ImportItemsAsync(string legacyNumber, long formId)
    {
        var items = await src.QueryAsync("""
            SELECT Imp_type AS Kind, Imp_text AS Descr, Imp_Harm AS HazardLabel, Imp_Harm_idx AS HazardId,
                   Imp_MT_idx AS TypeId, Imp_MI_idx AS ItemId, Imp_reason AS Reason, Imp_isPenalty AS Improved,
                   Imp_createdate_after AS ImprovedDate, Imp_Verify AS Verify, Imp_Verifyreason AS VerifyReason,
                   Imp_Ticket_id AS TicketNo
            FROM dbo.Improve WHERE RTRIM(Imp_Pr1_number) = @legacyNumber ORDER BY Imp_type, Imp_idx
            """, new { legacyNumber });

        var ordinals = new Dictionary<int, int>();
        foreach (var i in items)
        {
            int kind = (int)(i.Kind ?? 1);
            ordinals[kind] = ordinals.GetValueOrDefault(kind) + 1;
            await dst.ExecuteAsync("""
                INSERT dbo.Pr1Item (FormId, Kind, Ordinal, Description, HazardId, DeficiencyTypeId, DeficiencyItemId,
                    HazardLabel, Improved, NotImprovedReason, ImprovedDate, Verified, VerifyReason, PenaltyTicketNo)
                VALUES (@formId, @kind, @ordinal, @descr, @hazardId, @typeId, @itemId, @hazardLabel,
                    @improved, @reason, @improvedDate, @verified, @verifyReason, @ticketNo)
                """, new
            {
                formId,
                kind,
                ordinal = ordinals[kind],
                descr = Truncate((string?)i.Descr ?? "", 500),
                hazardId = (int?)i.HazardId,
                typeId = (int?)i.TypeId,
                itemId = (int?)i.ItemId,
                hazardLabel = Truncate((string?)i.HazardLabel, 300),
                improved = ToBool((string?)i.Improved),
                reason = Truncate((string?)i.Reason, 300),
                improvedDate = (DateTime?)i.ImprovedDate,
                verified = ToBool((string?)i.Verify),
                verifyReason = Truncate((string?)i.VerifyReason, 300),
                ticketNo = NullIfEmpty((string?)i.TicketNo),
            }, tx);
        }
    }

    private static bool? ToBool(string? s) => s switch { "T" => true, "F" => false, _ => null };
    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];

    private static string? LegacyImporter_CleanId(string? id, HashSet<string> valid)
    {
        id = NullIfEmpty(id);
        return id is not null && valid.Contains(id) ? id : null;
    }
}

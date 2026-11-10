using Dapper;
using Taipower.Safety.Core.Common;
using Taipower.Safety.Core.Forms.Pr1;
using Taipower.Safety.Core.Forms.Workflow;
using Taipower.Safety.Core.Org;

namespace Taipower.Safety.Infrastructure.Data;

/// <summary>
/// 把 <see cref="AssigneeRule"/> 解析成實際員工，取代 legacy 散落的
/// GetDispatchman / GetSir / GetMySupervisor 查詢，集中於此並加上 null 檢查。
/// 只依賴 <see cref="IWorkflowForm"/> 路由脈絡，因此 Pr1/Pr2 等表單共用同一顆 resolver。
/// </summary>
public sealed class AssigneeResolver(IDbConnectionFactory db, IOrgRepository org) : IAssigneeResolver
{
    public async Task<Employee?> ResolveAsync(IWorkflowForm form, AssigneeRule rule, string actorId, string? specificPersonId, CancellationToken ct = default)
    {
        var stage = form.Step.Stage;
        return rule switch
        {
            AssigneeRule.SafetyDesk => await DeskOfGroupAsync(Groups.Safety, ct),
            AssigneeRule.ImprovementDesk => form.InformGroupId is { Length: > 0 } g ? await DeskOfGroupAsync(g, ct) : null,
            AssigneeRule.SpecificPerson => await org.GetEmployeeAsync(specificPersonId ?? "", ct),
            AssigneeRule.AssignedChief => await ById(form.ChiefOfRecord(stage)),
            AssigneeRule.Manager => await ManagerOfGroupAsync(GroupOfStage(form), ct),
            AssigneeRule.ProxyManager => await ById(specificPersonId),
            AssigneeRule.OfficerOfRecord => await ById(form.OfficerOfRecord(stage)),
            AssigneeRule.ChiefOfRecord => await ById(form.ChiefOfRecord(stage)),
            AssigneeRule.ManagerOfRecord => await ById(form.ManagerOfRecord(stage)),
            AssigneeRule.Creator => await ById(form.CreatedBy),
            AssigneeRule.Leader => await ById(form.LeaderId),
            AssigneeRule.NextApprover => (await ResolveApprovalNextAsync(form, ct)).Approver,
            AssigneeRule.Self => await ById(form.CurrentHolderId),
            _ => null,
        };

        Task<Employee?> ById(string? id) => string.IsNullOrEmpty(id) ? Task.FromResult<Employee?>(null) : org.GetEmployeeAsync(id, ct);
    }

    private static string GroupOfStage(IWorkflowForm f) => f.Step.Stage switch
    {
        Stage.Improvement => f.InformGroupId ?? Groups.Safety,
        _ => Groups.Safety, // C3/C5 都在工安組
    };

    private async Task<Employee?> DeskOfGroupAsync(string groupId, CancellationToken ct)
    {
        // 登記桌 = 該組 Role 3 的人；若無則退回該組經理 (Rank 3)。
        var e = await FirstEmployeeAsync("e.GroupId = @groupId AND e.RoleId = @role", new { groupId, role = Roles.RegistrationDesk }, ct);
        return e ?? await ManagerOfGroupAsync(groupId, ct);
    }

    private Task<Employee?> ManagerOfGroupAsync(string groupId, CancellationToken ct) =>
        FirstEmployeeAsync("e.GroupId = @groupId AND e.RankId = @rank", new { groupId, rank = Ranks.Manager }, ct);

    private async Task<Employee?> FirstEmployeeAsync(string where, object args, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var id = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            $"SELECT TOP 1 e.EmployeeId FROM dbo.Employee e WHERE e.IsActive = 1 AND {where} ORDER BY e.EmployeeId",
            args, cancellationToken: ct));
        return id is null ? null : await org.GetEmployeeAsync(id, ct);
    }

    /// <summary>
    /// C6 長官鏈：巡查領隊 → 工程副處長(ENSir) → 工安副處長(WSSir) → 處長(TSir)，
    /// 依 legacy 去重規則 (領隊本身可能已是某位副處長或處長) 跳過重複者。
    /// 回傳下一關的位置與人員，讓流程把步驟推進到正確的批示層級。
    /// </summary>
    public async Task<(Position Position, Employee? Approver)> ResolveApprovalNextAsync(IWorkflowForm form, CancellationToken ct = default)
    {
        var leader = form.LeaderId;
        var engDeputy = await DeputyOfProjectGroupAsync(form, ct);   // 工程副處長：督促組別上層
        var safetyDeputy = await DeputyOfSafetyAsync(ct);            // 工安副處長：69 的上層(003)
        var director = await DirectorAsync(ct);                      // 處長

        (Position, Employee?) Dir() => (Position.Director, director);

        return form.Step.Position switch
        {
            Position.Leader when leader == director?.EmployeeId => Dir(),                       // 領隊即處長
            Position.Leader when Same(leader, engDeputy) && Same(leader, safetyDeputy) => Dir(),
            Position.Leader when Same(leader, engDeputy) => (Position.DeputySafety, safetyDeputy),
            Position.Leader => (Position.DeputyEng, engDeputy),
            Position.DeputyEng when Same(engDeputy?.EmployeeId, safetyDeputy) || Same(leader, safetyDeputy) => Dir(),
            Position.DeputyEng => (Position.DeputySafety, safetyDeputy),
            Position.DeputySafety => Dir(),
            _ => Dir(),
        };

        static bool Same(string? a, Employee? b) => b is not null && a == b.EmployeeId;
    }

    private async Task<Employee?> DeputyOfProjectGroupAsync(IWorkflowForm form, CancellationToken ct)
    {
        // 工程副處長 = 督促組別所屬副處長室 (ORG_Group 的上層) 的主管(副處長 Rank 2)。
        var groupId = form.InformGroupId ?? form.ImproveGroupId;
        if (string.IsNullOrEmpty(groupId)) return await DeputyOfSafetyAsync(ct);
        await using var conn = await db.OpenAsync(ct);
        var deputyGroup = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT ParentGroupId FROM dbo.OrgGroup WHERE GroupId = @groupId", new { groupId }, cancellationToken: ct));
        return await DeputyOfGroupAsync(deputyGroup, ct);
    }

    private async Task<Employee?> DeputyOfSafetyAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var parent = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT ParentGroupId FROM dbo.OrgGroup WHERE GroupId = @g", new { g = Groups.Safety }, cancellationToken: ct));
        return await DeputyOfGroupAsync(parent, ct);
    }

    private Task<Employee?> DeputyOfGroupAsync(string? deputyGroupId, CancellationToken ct) =>
        string.IsNullOrEmpty(deputyGroupId)
            ? Task.FromResult<Employee?>(null)
            : FirstEmployeeAsync("e.GroupId = @g AND e.RankId = @rank", new { g = deputyGroupId, rank = Ranks.DeputyDirector }, ct);

    private Task<Employee?> DirectorAsync(CancellationToken ct) =>
        FirstEmployeeAsync("e.GroupId = @g AND e.RankId = @rank", new { g = Groups.DirectorOffice, rank = Ranks.Director }, ct);
}

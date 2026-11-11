using Dapper;
using Taipower.Safety.Core.Common;
using Taipower.Safety.Core.Forms.Pr3;
using Taipower.Safety.Core.Org;

namespace Taipower.Safety.Infrastructure.Data;

/// <summary>
/// Pr3 的指派解析：把 <see cref="Pr3AssigneeRule"/> 解析成實際員工。
/// Pr3 走動態組別路由（辦理組別 Pr3_Group_id → 經辦組；工安組固定 69），
/// 與 Pr1/Pr2 的 C1→C6 resolver 分開。組織導航查詢暫各自一份（日後可抽 OrgNavigator 共用）。
/// </summary>
public sealed class Pr3AssigneeResolver(IDbConnectionFactory db, IOrgRepository org) : IPr3AssigneeResolver
{
    public async Task<Employee?> ResolveAsync(Pr3Form form, Pr3AssigneeRule rule, string actorId, string? specificPersonId, CancellationToken ct = default)
    {
        var groupId = form.Step.Stage == Pr3Stage.SafetyReview ? Groups.Safety : form.GroupId;
        return rule switch
        {
            Pr3AssigneeRule.Inspector => await ById(form.InspectorId),
            Pr3AssigneeRule.Filer => await ById(form.CreatedBy),
            Pr3AssigneeRule.HandlingDesk => await DeskOfGroupAsync(form.GroupId, ct),
            Pr3AssigneeRule.SafetyDesk => await DeskOfGroupAsync(Groups.Safety, ct),
            Pr3AssigneeRule.SpecificPerson => await ById(specificPersonId),
            Pr3AssigneeRule.AssignedChief => await ById(form.ChiefOfRecord(form.Step.Stage)),
            Pr3AssigneeRule.Manager => await ManagerOfGroupAsync(groupId, ct),
            Pr3AssigneeRule.OfficerOfRecord => await ById(form.OfficerOfRecord(form.Step.Stage)),
            Pr3AssigneeRule.Self => await ById(form.CurrentHolderId),
            _ => null,
        };

        Task<Employee?> ById(string? id) => string.IsNullOrEmpty(id) ? Task.FromResult<Employee?>(null) : org.GetEmployeeAsync(id, ct);
    }

    private async Task<Employee?> DeskOfGroupAsync(string groupId, CancellationToken ct)
    {
        // 登記桌 = 該組 Role 3；若無則退回該組經理 (Rank 3)。
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
}

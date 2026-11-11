using Dapper;
using Taipower.Safety.Core.Common;
using Taipower.Safety.Core.Forms.Pr4;
using Taipower.Safety.Core.Org;

namespace Taipower.Safety.Infrastructure.Data;

/// <summary>
/// Pr4 的指派解析。框線一課長/經理與框線二經辦組皆以單一經辦組別 GroupId 解析
/// （規格未明確區分，為設計推斷）。組織導航查詢暫各自一份（日後可抽 OrgNavigator 共用）。
/// </summary>
public sealed class Pr4AssigneeResolver(IDbConnectionFactory db, IOrgRepository org) : IPr4AssigneeResolver
{
    public async Task<Employee?> ResolveAsync(Pr4Form form, Pr4AssigneeRule rule, string actorId, string? specificPersonId, CancellationToken ct = default)
    {
        return rule switch
        {
            Pr4AssigneeRule.GroupChief => await FirstEmployeeAsync("e.GroupId = @g AND e.RankId = @rank", new { g = form.GroupId, rank = Ranks.SectionChief }, ct),
            Pr4AssigneeRule.GroupManager => await FirstEmployeeAsync("e.GroupId = @g AND e.RankId = @rank", new { g = form.GroupId, rank = Ranks.Manager }, ct),
            Pr4AssigneeRule.HandlingDesk => await DeskOfGroupAsync(form.GroupId, ct),
            Pr4AssigneeRule.SpecificPerson => await ById(specificPersonId),
            Pr4AssigneeRule.AssignedChief => await ById(form.ChiefOfRecord(form.Step.Stage)),
            Pr4AssigneeRule.OfficerOfRecord => await ById(form.OfficerOfRecord(form.Step.Stage)),
            Pr4AssigneeRule.Creator => await ById(form.CreatedBy),
            Pr4AssigneeRule.Self => await ById(form.CurrentHolderId),
            _ => null,
        };

        Task<Employee?> ById(string? id) => string.IsNullOrEmpty(id) ? Task.FromResult<Employee?>(null) : org.GetEmployeeAsync(id, ct);
    }

    private async Task<Employee?> DeskOfGroupAsync(string groupId, CancellationToken ct)
    {
        var e = await FirstEmployeeAsync("e.GroupId = @groupId AND e.RoleId = @role", new { groupId, role = Roles.RegistrationDesk }, ct);
        return e ?? await FirstEmployeeAsync("e.GroupId = @groupId AND e.RankId = @rank", new { groupId, rank = Ranks.Manager }, ct);
    }

    private async Task<Employee?> FirstEmployeeAsync(string where, object args, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var id = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            $"SELECT TOP 1 e.EmployeeId FROM dbo.Employee e WHERE e.IsActive = 1 AND {where} ORDER BY e.EmployeeId",
            args, cancellationToken: ct));
        return id is null ? null : await org.GetEmployeeAsync(id, ct);
    }
}

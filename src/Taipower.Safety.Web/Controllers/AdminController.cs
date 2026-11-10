using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Taipower.Safety.Core.Common;
using Taipower.Safety.Core.Org;
using Taipower.Safety.Core.Security;
using Taipower.Safety.Web.Infrastructure;

namespace Taipower.Safety.Web.Controllers;

public sealed class OrgPageModel
{
    public IReadOnlyList<OrgGroup> Groups { get; init; } = [];
    public IReadOnlyList<OrgSection> Sections { get; init; } = [];
}

public sealed class RankRolePageModel
{
    public IReadOnlyList<OrgRank> Ranks { get; init; } = [];
    public IReadOnlyList<OrgRole> Roles { get; init; } = [];
}

public sealed class EmployeeListModel
{
    public EmployeeQuery Query { get; init; } = new();
    public IReadOnlyList<Employee> Employees { get; init; } = [];
    public IReadOnlyList<OrgGroup> Groups { get; init; } = [];
    public IReadOnlyList<OrgRank> Ranks { get; init; } = [];
    public IReadOnlyList<OrgRole> Roles { get; init; } = [];
}

public sealed class EmployeeEditModel
{
    public bool IsNew { get; set; }

    [Required(ErrorMessage = "請輸入員工代號"), RegularExpression(@"^[A-Za-z0-9]{1,6}$", ErrorMessage = "員工代號為 1~6 碼英數字")]
    public string EmployeeId { get; set; } = "";

    [Required(ErrorMessage = "請輸入姓名"), StringLength(20)]
    public string Name { get; set; } = "";

    [EmailAddress(ErrorMessage = "Email 格式錯誤"), StringLength(100)]
    public string? Email { get; set; }

    public string? GroupId { get; set; }
    public string? SectionCode { get; set; }
    public int? RankId { get; set; }
    public int? RoleId { get; set; }
    public bool IsActive { get; set; } = true;

    [RegularExpression(@"^[A-Za-z0-9_.\-]{1,20}$", ErrorMessage = "帳號為 1~20 碼英數字")]
    public string? Account { get; set; }
    public bool HasAccount { get; set; }

    /// <summary>New password (create account or reset). Blank = unchanged.</summary>
    [MinLength(8, ErrorMessage = "密碼至少 8 碼")]
    public string? NewPassword { get; set; }

    public IReadOnlyList<OrgGroup> Groups { get; set; } = [];
    public IReadOnlyList<OrgSection> Sections { get; set; } = [];
    public IReadOnlyList<OrgRank> Ranks { get; set; } = [];
    public IReadOnlyList<OrgRole> Roles { get; set; } = [];
}

public sealed class PermissionPageModel
{
    public int RoleId { get; init; }
    public IReadOnlyList<OrgRole> Roles { get; init; } = [];
    public IReadOnlyList<Feature> Features { get; init; } = [];
    public IReadOnlySet<int> Granted { get; init; } = new HashSet<int>();
}

public sealed class AdminController(
    IOrgRepository org,
    IPermissionRepository permissions,
    IAccountRepository accounts,
    IAuditLogQuery auditQuery) : AppController
{
    // ----- Groups & sections -----
    [RequireFeature(Features.AdminOrg)]
    public async Task<IActionResult> Org(CancellationToken ct) =>
        View(new OrgPageModel { Groups = await org.GetGroupsAsync(ct), Sections = await org.GetSectionsAsync(null, ct) });

    [RequireFeature(Features.AdminOrg), HttpPost]
    public async Task<IActionResult> SaveGroup(string groupId, string name, string? parentGroupId, int sortOrder, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(groupId) || groupId.Length > 3 || string.IsNullOrWhiteSpace(name))
        {
            Flash("組別代碼 (1~3 碼) 與名稱為必填", "danger");
            return RedirectToAction(nameof(Org));
        }
        await org.SaveGroupAsync(new OrgGroup(groupId.Trim(), name.Trim(), string.IsNullOrWhiteSpace(parentGroupId) ? null : parentGroupId, sortOrder), ct);
        await AuditAsync("組織維護", groupId, $"儲存組別 {name}");
        Flash("組別已儲存");
        return RedirectToAction(nameof(Org));
    }

    [RequireFeature(Features.AdminOrg), HttpPost]
    public async Task<IActionResult> SaveSection(string groupId, string sectionCode, string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(groupId) || string.IsNullOrWhiteSpace(sectionCode) || sectionCode.Length > 3 || string.IsNullOrWhiteSpace(name))
        {
            Flash("組別、課別代碼 (1~3 碼) 與名稱為必填", "danger");
            return RedirectToAction(nameof(Org));
        }
        await org.SaveSectionAsync(new OrgSection(groupId, sectionCode.Trim(), name.Trim()), ct);
        await AuditAsync("組織維護", $"{groupId}-{sectionCode}", $"儲存課別 {name}");
        Flash("課別已儲存");
        return RedirectToAction(nameof(Org));
    }

    // ----- Ranks & roles -----
    [RequireFeature(Features.AdminRankRole)]
    public async Task<IActionResult> RankRole(CancellationToken ct) =>
        View(new RankRolePageModel { Ranks = await org.GetRanksAsync(ct), Roles = await org.GetRolesAsync(ct) });

    [RequireFeature(Features.AdminRankRole), HttpPost]
    public async Task<IActionResult> SaveRank(int rankId, string name, int level, CancellationToken ct)
    {
        if (rankId <= 0 || string.IsNullOrWhiteSpace(name)) { Flash("職務代碼與名稱為必填", "danger"); return RedirectToAction(nameof(RankRole)); }
        await org.SaveRankAsync(new OrgRank(rankId, name.Trim(), level), ct);
        await AuditAsync("職務角色維護", $"Rank {rankId}", name);
        Flash("職務已儲存");
        return RedirectToAction(nameof(RankRole));
    }

    [RequireFeature(Features.AdminRankRole), HttpPost]
    public async Task<IActionResult> SaveRole(int roleId, string name, CancellationToken ct)
    {
        if (roleId <= 0 || string.IsNullOrWhiteSpace(name)) { Flash("角色代碼與名稱為必填", "danger"); return RedirectToAction(nameof(RankRole)); }
        await org.SaveRoleAsync(new OrgRole(roleId, name.Trim()), ct);
        await AuditAsync("職務角色維護", $"Role {roleId}", name);
        Flash("角色已儲存");
        return RedirectToAction(nameof(RankRole));
    }

    // ----- Employees & accounts -----
    [RequireFeature(Features.AdminEmployee)]
    public async Task<IActionResult> Employees([FromQuery] EmployeeQuery query, CancellationToken ct)
    {
        query.IncludeInactive = true;
        return View(new EmployeeListModel
        {
            Query = query,
            Employees = await org.FindEmployeesAsync(query, ct),
            Groups = await org.GetGroupsAsync(ct),
            Ranks = await org.GetRanksAsync(ct),
            Roles = await org.GetRolesAsync(ct),
        });
    }

    [RequireFeature(Features.AdminEmployee)]
    public async Task<IActionResult> EditEmployee(string? id, CancellationToken ct)
    {
        var model = new EmployeeEditModel { IsNew = string.IsNullOrEmpty(id) };
        if (!model.IsNew)
        {
            var e = await org.GetEmployeeAsync(id!, ct);
            if (e is null) return NotFound();
            model.EmployeeId = e.EmployeeId;
            model.Name = e.Name;
            model.Email = e.Email;
            model.GroupId = e.GroupId;
            model.SectionCode = e.SectionCode;
            model.RankId = e.RankId;
            model.RoleId = e.RoleId;
            model.IsActive = e.IsActive;
            model.Account = e.Account;
            model.HasAccount = e.Account is not null;
        }
        await FillLookups(model, ct);
        return View(model);
    }

    [RequireFeature(Features.AdminEmployee), HttpPost]
    public async Task<IActionResult> EditEmployee(EmployeeEditModel model, CancellationToken ct)
    {
        var existing = await org.GetEmployeeAsync(model.EmployeeId, ct);
        if (model.IsNew && existing is not null)
            ModelState.AddModelError(nameof(model.EmployeeId), "員工代號已存在");
        model.HasAccount = existing?.Account is not null;
        if (!model.HasAccount && !string.IsNullOrWhiteSpace(model.Account) && string.IsNullOrWhiteSpace(model.NewPassword))
            ModelState.AddModelError(nameof(model.NewPassword), "建立帳號時須設定密碼");
        if (!model.HasAccount && !string.IsNullOrWhiteSpace(model.Account) && await accounts.FindAsync(model.Account, ct) is not null)
            ModelState.AddModelError(nameof(model.Account), "帳號已被使用");

        if (!ModelState.IsValid)
        {
            await FillLookups(model, ct);
            return View(model);
        }

        await org.SaveEmployeeAsync(new Employee
        {
            EmployeeId = model.EmployeeId.Trim(),
            Name = model.Name.Trim(),
            Email = model.Email?.Trim(),
            GroupId = model.GroupId,
            SectionCode = model.SectionCode,
            RankId = model.RankId,
            RoleId = model.RoleId,
            IsActive = model.IsActive,
        }, Me.EmployeeId, ct);

        if (!model.HasAccount && !string.IsNullOrWhiteSpace(model.Account))
        {
            await accounts.CreateAsync(model.Account.Trim(), model.EmployeeId, PasswordHasher.Hash(model.NewPassword!), ct);
            await AuditAsync("人員資料維護", model.EmployeeId, $"建立帳號 {model.Account}");
        }
        else if (model.HasAccount && !string.IsNullOrWhiteSpace(model.NewPassword))
        {
            await accounts.SetPasswordAsync(existing!.Account!, PasswordHasher.Hash(model.NewPassword), ct);
            await AuditAsync("人員資料維護", model.EmployeeId, "重設密碼");
        }

        await AuditAsync("人員資料維護", model.EmployeeId, model.IsNew ? "新增人員" : "修改人員");
        Flash("人員資料已儲存");
        return RedirectToAction(nameof(Employees));
    }

    private async Task FillLookups(EmployeeEditModel m, CancellationToken ct)
    {
        m.Groups = await org.GetGroupsAsync(ct);
        m.Sections = await org.GetSectionsAsync(null, ct);
        m.Ranks = await org.GetRanksAsync(ct);
        m.Roles = await org.GetRolesAsync(ct);
    }

    // ----- Role permissions -----
    [RequireFeature(Features.AdminPermission)]
    public async Task<IActionResult> Permissions(int? roleId, CancellationToken ct)
    {
        var roles = await org.GetRolesAsync(ct);
        var selected = roleId ?? roles.FirstOrDefault()?.RoleId ?? 0;
        return View(new PermissionPageModel
        {
            RoleId = selected,
            Roles = roles,
            Features = await permissions.GetFeaturesAsync(ct),
            Granted = await permissions.GetRoleFeaturesAsync(selected, ct),
        });
    }

    [RequireFeature(Features.AdminPermission), HttpPost]
    public async Task<IActionResult> SavePermissions(int roleId, int[] featureIds, CancellationToken ct)
    {
        await permissions.SetRoleFeaturesAsync(roleId, featureIds ?? [], ct);
        await AuditAsync("角色權限設定", $"Role {roleId}", string.Join(',', featureIds ?? []));
        Flash("權限已儲存，使用者重新登入後生效");
        return RedirectToAction(nameof(Permissions), new { roleId });
    }

    // ----- Audit log -----
    [RequireFeature(Features.AdminAuditLog)]
    public async Task<IActionResult> AuditLog([FromQuery] AuditLogQuery query, CancellationToken ct)
    {
        ViewBag.Query = query;
        return View(await auditQuery.SearchAsync(query, ct));
    }
}

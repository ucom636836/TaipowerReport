using Microsoft.AspNetCore.Mvc;
using Taipower.Safety.Core.Reference;
using Taipower.Safety.Core.Security;
using Taipower.Safety.Web.Infrastructure;

namespace Taipower.Safety.Web.Controllers;

public sealed class ReferencePageModel
{
    public IReadOnlyList<EngineeringProject> Projects { get; init; } = [];
    public IReadOnlyList<Contractor> Contractors { get; init; } = [];
    public IReadOnlyList<Hazard> Hazards { get; init; } = [];
    public IReadOnlyList<DeficiencyType> Types { get; init; } = [];
    public IReadOnlyList<DeficiencyItem> Items { get; init; } = [];
    public IReadOnlyList<PenaltyVersion> PenaltyVersions { get; init; } = [];
}

[RequireFeature(Features.AdminReference)]
public sealed class ReferenceController(IReferenceRepository refs) : AppController
{
    public async Task<IActionResult> Index(CancellationToken ct) => View(new ReferencePageModel
    {
        Projects = await refs.GetProjectsAsync(activeOnly: false, ct),
        Contractors = await refs.GetContractorsAsync(null, activeOnly: false, ct),
        Hazards = await refs.GetHazardsAsync(ct),
        Types = await refs.GetDeficiencyTypesAsync(null, ct),
        Items = await refs.GetDeficiencyItemsAsync(null, ct),
        PenaltyVersions = await refs.GetPenaltyVersionsAsync(ct),
    });

    public async Task<IActionResult> EditProject(int? id, CancellationToken ct)
    {
        var p = id is null ? new EngineeringProject() : await refs.GetProjectAsync(id.Value, ct);
        if (p is null) return NotFound();
        ViewBag.Contractors = await refs.GetContractorsAsync(null, activeOnly: false, ct);
        ViewBag.PenaltyVersions = await refs.GetPenaltyVersionsAsync(ct);
        return View(p);
    }

    [HttpPost]
    public async Task<IActionResult> EditProject(EngineeringProject p, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(p.ProjectNumber)) ModelState.AddModelError(nameof(p.ProjectNumber), "請輸入工程編號");
        if (string.IsNullOrWhiteSpace(p.Name)) ModelState.AddModelError(nameof(p.Name), "請輸入工程名稱");
        if (!ModelState.IsValid)
        {
            ViewBag.Contractors = await refs.GetContractorsAsync(null, activeOnly: false, ct);
            ViewBag.PenaltyVersions = await refs.GetPenaltyVersionsAsync(ct);
            return View(p);
        }
        var id = await refs.SaveProjectAsync(p, ct);
        await AuditAsync("基本資料維護", $"Project {id}", $"儲存工程 {p.ProjectNumber} {p.Name}");
        Flash("工程已儲存");
        return RedirectToAction(nameof(Index), null, "projects");
    }

    public async Task<IActionResult> EditContractor(int? id, CancellationToken ct)
    {
        var c = id is null ? new Contractor() : await refs.GetContractorAsync(id.Value, ct);
        if (c is null) return NotFound();
        while (c.Contacts.Count < 3) c.Contacts.Add(new ContractorContact());
        return View(c);
    }

    [HttpPost]
    public async Task<IActionResult> EditContractor(Contractor c, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(c.Name)) ModelState.AddModelError(nameof(c.Name), "請輸入承攬商名稱");
        for (var i = 0; i < c.Contacts.Count; i++)
        {
            var email = c.Contacts[i].Email;
            if (!string.IsNullOrWhiteSpace(email) && !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(email))
                ModelState.AddModelError($"Contacts[{i}].Email", "Email 格式錯誤");
        }
        if (!ModelState.IsValid) return View(c);
        var id = await refs.SaveContractorAsync(c, ct);
        await AuditAsync("基本資料維護", $"Contractor {id}", $"儲存承攬商 {c.Name}");
        Flash("承攬商已儲存");
        return RedirectToAction(nameof(Index), null, "contractors");
    }

    [HttpPost]
    public async Task<IActionResult> SaveHazard(int hazardId, string name, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            await refs.SaveHazardAsync(new Hazard(hazardId, name.Trim()), ct);
            await AuditAsync("基本資料維護", $"Hazard {hazardId}", name);
        }
        return RedirectToAction(nameof(Index), null, "catalogue");
    }

    [HttpPost]
    public async Task<IActionResult> SaveDeficiencyType(int deficiencyTypeId, int hazardId, string name, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(name) && hazardId > 0)
        {
            await refs.SaveDeficiencyTypeAsync(new DeficiencyType(deficiencyTypeId, hazardId, name.Trim()), ct);
            await AuditAsync("基本資料維護", $"DeficiencyType {deficiencyTypeId}", name);
        }
        return RedirectToAction(nameof(Index), null, "catalogue");
    }

    [HttpPost]
    public async Task<IActionResult> SaveDeficiencyItem(int deficiencyItemId, int deficiencyTypeId, string name, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(name) && deficiencyTypeId > 0)
        {
            await refs.SaveDeficiencyItemAsync(new DeficiencyItem(deficiencyItemId, deficiencyTypeId, name.Trim()), ct);
            await AuditAsync("基本資料維護", $"DeficiencyItem {deficiencyItemId}", name);
        }
        return RedirectToAction(nameof(Index), null, "catalogue");
    }
}

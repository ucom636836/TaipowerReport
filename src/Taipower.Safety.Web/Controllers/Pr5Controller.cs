using Microsoft.AspNetCore.Mvc;
using Taipower.Safety.Core.Common;
using Taipower.Safety.Core.Forms.Pr1;
using Taipower.Safety.Core.Forms.Pr5;
using Taipower.Safety.Core.Reference;
using Taipower.Safety.Core.Security;
using Taipower.Safety.Web.Infrastructure;
using Taipower.Safety.Web.Models;

namespace Taipower.Safety.Web.Controllers;

public sealed class Pr5Controller(
    Pr5Service service,
    IPr5Repository repo,
    IReferenceRepository refs,
    IPr1Repository pr1) : AppController
{
    [RequireFeature(Features.Pr5Query, Features.Pr5Create)]
    public async Task<IActionResult> Index([FromQuery] Pr5SearchQuery query, CancellationToken ct)
    {
        var rows = await repo.SearchAsync(query, ct);
        ViewBag.Query = query;
        ViewBag.Projects = await refs.GetProjectsAsync(false, ct);
        ViewBag.Contractors = await refs.GetContractorsAsync(null, false, ct);
        return View(rows);
    }

    [RequireFeature(Features.Pr5Create)]
    public async Task<IActionResult> Create(long? sourceFormId, long? sourceItemId, CancellationToken ct)
    {
        var vm = new Pr5CreateViewModel();
        // 由 Pr1 缺失項連動：預填工程、承攬商、缺失分類與備註。
        if (sourceFormId is { } fid && sourceItemId is { } iid)
        {
            var form = await pr1.GetAsync(fid, ct);
            var item = form?.Items.FirstOrDefault(i => i.ItemId == iid);
            if (form is not null && item is not null)
            {
                vm.Source = Pr5Source.Pr1;
                vm.SourceFormId = fid;
                vm.SourceItemId = iid;
                vm.SourceFormNumber = form.DisplayNumber;
                vm.ProjectId = form.ProjectId;
                vm.ContractorId = form.ContractorId;
                vm.HazardId = item.HazardId;
                vm.DeficiencyTypeId = item.DeficiencyTypeId;
                vm.DeficiencyItemId = item.DeficiencyItemId;
                vm.ViolationDate = form.PatrolDate;
                vm.Remark = item.Description;
            }
        }
        await FillLookups(ct);
        return View(vm);
    }

    [RequireFeature(Features.Pr5Create), HttpPost]
    public async Task<IActionResult> Create(Pr5CreateViewModel vm, CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            await FillLookups(ct);
            return View(vm);
        }

        var result = await service.CreateAsync(new Pr5CreateRequest(
            vm.Source, vm.SourceFormId, vm.SourceItemId, vm.ProjectId, vm.ContractorId, vm.AuditGroupId,
            vm.HazardId, vm.DeficiencyTypeId, vm.DeficiencyItemId, vm.ViolationDate, vm.Place?.Trim(), vm.UnitPeriod?.Trim(),
            vm.PenaltyItemCode?.Trim(), vm.PenaltyAmount, vm.ImproveDeadline, vm.Remark?.Trim(), vm.Notify), Me, ct);

        await AuditAsync("工程罰款-開立", result.PenaltyId.ToString(), result.Ok ? "成功" : result.Error, result.Ok);
        if (!result.Ok)
        {
            ModelState.AddModelError("", result.Error!);
            await FillLookups(ct);
            return View(vm);
        }
        Flash(vm.Notify ? "罰單已開立，並已嘗試通知承攬商。" : "罰單已開立。");
        return RedirectToAction(nameof(Detail), new { id = result.PenaltyId });
    }

    [RequireFeature(Features.Pr5Query, Features.Pr5Create)]
    public async Task<IActionResult> Detail(long id, CancellationToken ct)
    {
        var penalty = await repo.GetAsync(id, ct);
        if (penalty is null) return NotFound();
        return View(new Pr5DetailViewModel
        {
            Penalty = penalty,
            Project = await refs.GetProjectAsync(penalty.ProjectId, ct),
            Contractor = await refs.GetContractorAsync(penalty.ContractorId, ct),
            CanManage = User.HasFeature(Features.Pr5Create),
        });
    }

    [RequireFeature(Features.Pr5Create), HttpPost]
    public Task<IActionResult> Void(long id, CancellationToken ct) => StatusActionAsync(id, "作廢", () => service.VoidAsync(id, Me, ct), ct);

    [RequireFeature(Features.Pr5Create), HttpPost]
    public Task<IActionResult> MarkReceived(long id, CancellationToken ct) => StatusActionAsync(id, "簽收", () => service.MarkReceivedAsync(id, Me, ct), ct);

    [RequireFeature(Features.Pr5Create), HttpPost]
    public Task<IActionResult> Notify(long id, CancellationToken ct) => StatusActionAsync(id, "通知承攬商", () => service.NotifyAsync(id, Me, ct), ct);

    private async Task<IActionResult> StatusActionAsync(long id, string label, Func<Task<Pr5ActionResult>> act, CancellationToken ct)
    {
        var result = await act();
        await AuditAsync($"工程罰款-{label}", id.ToString(), result.Ok ? "成功" : result.Error, result.Ok);
        Flash(result.Ok ? $"已{label}。" : result.Error!, result.Ok ? "success" : "danger");
        return RedirectToAction(nameof(Detail), new { id });
    }

    private async Task FillLookups(CancellationToken ct)
    {
        ViewBag.Projects = await refs.GetProjectsAsync(true, ct);
        ViewBag.Contractors = await refs.GetContractorsAsync(null, true, ct);
        ViewBag.Hazards = await refs.GetHazardsAsync(ct);
        ViewBag.DeficiencyTypes = await refs.GetDeficiencyTypesAsync(null, ct);
        ViewBag.DeficiencyItems = await refs.GetDeficiencyItemsAsync(null, ct);
    }
}

using Microsoft.AspNetCore.Mvc;
using Taipower.Safety.Core.Common;
using Taipower.Safety.Core.Forms.Pr4;
using Taipower.Safety.Core.Forms.Workflow;
using Taipower.Safety.Core.Org;
using Taipower.Safety.Core.Reference;
using Taipower.Safety.Core.Security;
using Taipower.Safety.Web.Infrastructure;
using Taipower.Safety.Web.Models;

namespace Taipower.Safety.Web.Controllers;

public sealed class Pr4Controller(
    Pr4Service service,
    IPr4Repository repo,
    IReferenceRepository refs,
    IOrgRepository org) : AppController
{
    // 查詢
    [RequireFeature(Features.Pr4Query)]
    public async Task<IActionResult> Index([FromQuery] Pr4SearchQuery query, CancellationToken ct)
    {
        var canSeeAll = User.HasFeature(Features.AdminAuditLog);
        var rows = await repo.SearchAsync(query, Me.EmployeeId, canSeeAll, ct);
        ViewBag.Query = query;
        ViewBag.Projects = await refs.GetProjectsAsync(false, ct);
        ViewBag.Contractors = await refs.GetContractorsAsync(null, false, ct);
        return View(rows);
    }

    // 開單（檢驗員）
    [RequireFeature(Features.Pr4Create)]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        await FillCreateLookups(ct);
        return View(new Pr4CreateViewModel { SpotCheckDate = DateTime.Today });
    }

    [RequireFeature(Features.Pr4Create), HttpPost]
    public async Task<IActionResult> Create(Pr4CreateViewModel vm, CancellationToken ct)
    {
        var items = vm.ToItems();
        if (items.Count == 0) ModelState.AddModelError("", "請至少填寫一項優點、缺失或建議改善事項。");
        if (!ModelState.IsValid)
        {
            await FillCreateLookups(ct);
            return View(vm);
        }

        var result = await service.CreateAsync(new Pr4CreateRequest(
            vm.SpotCheckDate, vm.ProjectId, vm.ContractorId, vm.Location, vm.ArriveTime, vm.LeaveTime,
            vm.GroupId, vm.ImproveDeadline, vm.NeedImprove, items), Me, ct);

        await AuditAsync("工安抽查-開單", result.FormId.ToString(), result.Ok ? "建單成功" : result.Error, result.Ok);
        if (!result.Ok)
        {
            ModelState.AddModelError("", result.Error!);
            await FillCreateLookups(ct);
            return View(vm);
        }
        Flash("建單完成，已送出陳核。");
        return RedirectToAction(nameof(Form), new { id = result.FormId });
    }

    // 表單檢視 + 動作
    [RequireFeature(Features.Pr4Query, Features.Pr4Create, Features.Pr4Inspection, Features.Pr4Handling)]
    public async Task<IActionResult> Form(long id, CancellationToken ct)
    {
        var form = await repo.GetAsync(id, ct);
        if (form is null) return NotFound();

        if (form.PendingRecipientId == Me.EmployeeId && form.CurrentHolderId != Me.EmployeeId)
        {
            await service.OpenAsync(id, Me, ct);
            form = await repo.GetAsync(id, ct);
        }

        var vm = new Pr4FormViewModel
        {
            Form = form!,
            Project = await refs.GetProjectAsync(form!.ProjectId, ct),
            Contractor = await refs.GetContractorAsync(form.ContractorId, ct),
            Inspector = await org.GetEmployeeAsync(form.CreatedBy, ct),
            History = await repo.GetHistoryAsync(id, ct),
            CanAct = form.Status == FormStatus.InProgress && form.CurrentHolderId == Me.EmployeeId && form.PendingRecipientId == Me.EmployeeId,
            Allowed = Pr4Workflow.AllowedActions(form.Step),
        };
        return View(vm);
    }

    [HttpPost]
    public async Task<IActionResult> Act(Pr4ActRequest req, CancellationToken ct)
    {
        var result = await service.ActAsync(new Pr4ActionRequest(
            req.FormId, req.Action, req.Comment, req.AssigneeId, req.IsJump), Me, ct);

        await AuditAsync($"工安抽查-{req.Action}", req.FormId.ToString(), result.Ok ? "成功" : result.Error, result.Ok);
        Flash(result.Ok ? "已完成。" : result.Error!, result.Ok ? "success" : "danger");
        return RedirectToAction(nameof(Form), new { id = req.FormId });
    }

    [RequireFeature(Features.Pr4Create)]
    public async Task<IActionResult> Contractors(int projectId, CancellationToken ct) =>
        Json((await refs.GetContractorsAsync(projectId, true, ct)).Select(c => new { c.ContractorId, c.Name }));

    private async Task FillCreateLookups(CancellationToken ct)
    {
        ViewBag.Projects = await refs.GetProjectsAsync(true, ct);
        ViewBag.Contractors = await refs.GetContractorsAsync(null, true, ct);
        ViewBag.Groups = await org.GetGroupsAsync(ct);
        ViewBag.Hazards = await refs.GetHazardsAsync(ct);
    }
}

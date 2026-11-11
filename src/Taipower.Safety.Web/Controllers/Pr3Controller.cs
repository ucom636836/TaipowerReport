using Microsoft.AspNetCore.Mvc;
using Taipower.Safety.Core.Common;
using Taipower.Safety.Core.Forms.Pr3;
using Taipower.Safety.Core.Forms.Workflow;
using Taipower.Safety.Core.Org;
using Taipower.Safety.Core.Reference;
using Taipower.Safety.Core.Security;
using Taipower.Safety.Web.Infrastructure;
using Taipower.Safety.Web.Models;

namespace Taipower.Safety.Web.Controllers;

public sealed class Pr3Controller(
    Pr3Service service,
    IPr3Repository repo,
    IReferenceRepository refs,
    IOrgRepository org) : AppController
{
    // 查詢
    [RequireFeature(Features.Pr3Query)]
    public async Task<IActionResult> Index([FromQuery] Pr3SearchQuery query, CancellationToken ct)
    {
        var canSeeAll = User.HasFeature(Features.AdminAuditLog);
        var rows = await repo.SearchAsync(query, Me.EmployeeId, canSeeAll, ct);
        ViewBag.Query = query;
        ViewBag.Projects = await refs.GetProjectsAsync(false, ct);
        ViewBag.Contractors = await refs.GetContractorsAsync(null, false, ct);
        return View(rows);
    }

    // 開單
    [RequireFeature(Features.Pr3Create)]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        await FillCreateLookups(ct);
        return View(new Pr3CreateViewModel { InspectDate = DateTime.Today });
    }

    [RequireFeature(Features.Pr3Create), HttpPost]
    public async Task<IActionResult> Create(Pr3CreateViewModel vm, CancellationToken ct)
    {
        var items = vm.ToItems();
        if (items.Count == 0) ModelState.AddModelError("", "請至少填寫一項優點、缺點或建議改善事項。");
        if (!ModelState.IsValid)
        {
            await FillCreateLookups(ct);
            return View(vm);
        }

        var result = await service.CreateAsync(new Pr3CreateRequest(
            vm.InspectorId, vm.InspectDate, vm.InspectTime, vm.Attendants, vm.Location, vm.Target,
            vm.ProjectId, vm.ContractorId, vm.GroupId, vm.ImproveDeadline, items), Me, ct);

        await AuditAsync("走動管理-開單", result.FormId.ToString(), result.Ok ? "建單成功" : result.Error, result.Ok);
        if (!result.Ok)
        {
            ModelState.AddModelError("", result.Error!);
            await FillCreateLookups(ct);
            return View(vm);
        }
        Flash("建單完成，已送出通知。");
        return RedirectToAction(nameof(Form), new { id = result.FormId });
    }

    // 表單檢視 + 動作
    [RequireFeature(Features.Pr3Query, Features.Pr3Create, Features.Pr3InspectorConfirm, Features.Pr3HandlingReview, Features.Pr3SafetyReview)]
    public async Task<IActionResult> Form(long id, CancellationToken ct)
    {
        var form = await repo.GetAsync(id, ct);
        if (form is null) return NotFound();

        if (form.PendingRecipientId == Me.EmployeeId && form.CurrentHolderId != Me.EmployeeId)
        {
            await service.OpenAsync(id, Me, ct);
            form = await repo.GetAsync(id, ct);
        }

        var vm = new Pr3FormViewModel
        {
            Form = form!,
            Project = await refs.GetProjectAsync(form!.ProjectId, ct),
            Contractor = await refs.GetContractorAsync(form.ContractorId, ct),
            Inspector = await org.GetEmployeeAsync(form.InspectorId, ct),
            History = await repo.GetHistoryAsync(id, ct),
            CanAct = form.Status == FormStatus.InProgress && form.CurrentHolderId == Me.EmployeeId && form.PendingRecipientId == Me.EmployeeId,
            Allowed = Pr3Workflow.AllowedActions(form.Step),
        };
        return View(vm);
    }

    [HttpPost]
    public async Task<IActionResult> Act(Pr3ActRequest req, CancellationToken ct)
    {
        var result = await service.ActAsync(new Pr3ActionRequest(
            req.FormId, req.Action, req.Comment, req.AssigneeId, req.IsJump), Me, ct);

        await AuditAsync($"走動管理-{req.Action}", req.FormId.ToString(), result.Ok ? "成功" : result.Error, result.Ok);
        Flash(result.Ok ? "已完成。" : result.Error!, result.Ok ? "success" : "danger");
        return RedirectToAction(nameof(Form), new { id = req.FormId });
    }

    // ----- 下拉輔助 (AJAX) -----
    [RequireFeature(Features.Pr3Create)]
    public async Task<IActionResult> Contractors(int projectId, CancellationToken ct) =>
        Json((await refs.GetContractorsAsync(projectId, true, ct)).Select(c => new { c.ContractorId, c.Name }));

    private async Task FillCreateLookups(CancellationToken ct)
    {
        ViewBag.Projects = await refs.GetProjectsAsync(true, ct);
        ViewBag.Contractors = await refs.GetContractorsAsync(null, true, ct);
        ViewBag.Groups = await org.GetGroupsAsync(ct);
        ViewBag.Inspectors = await org.FindEmployeesAsync(new EmployeeQuery(), ct);
        ViewBag.Hazards = await refs.GetHazardsAsync(ct);
    }
}

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Taipower.Safety.Core.Common;
using Taipower.Safety.Core.Forms.Pr1;
using Taipower.Safety.Core.Org;
using Taipower.Safety.Core.Reference;
using Taipower.Safety.Core.Security;
using Taipower.Safety.Web.Infrastructure;
using Taipower.Safety.Web.Models;

using Taipower.Safety.Core.Forms.Workflow;
namespace Taipower.Safety.Web.Controllers;

public sealed class Pr1Controller(
    Pr1Service service,
    IPr1Repository repo,
    IReferenceRepository refs,
    IOrgRepository org) : AppController
{
    // 查詢
    [RequireFeature(Features.Pr1Query)]
    public async Task<IActionResult> Index([FromQuery] Pr1SearchQuery query, CancellationToken ct)
    {
        var canSeeAll = User.HasFeature(Features.AdminAuditLog);
        var rows = await repo.SearchAsync(query, Me.EmployeeId, canSeeAll, ct);
        ViewBag.Query = query;
        ViewBag.Projects = await refs.GetProjectsAsync(false, ct);
        ViewBag.Contractors = await refs.GetContractorsAsync(null, false, ct);
        return View(rows);
    }

    // 開單
    [RequireFeature(Features.Pr1Create)]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        await FillCreateLookups(ct);
        return View(new Pr1CreateViewModel { PatrolDate = DateTime.Today });
    }

    [RequireFeature(Features.Pr1Create), HttpPost]
    public async Task<IActionResult> Create(Pr1CreateViewModel vm, CancellationToken ct)
    {
        var items = vm.ToItems();
        if (items.Count == 0) ModelState.AddModelError("", "請至少填寫一項優點、缺點或建議改善事項。");
        if (!ModelState.IsValid)
        {
            await FillCreateLookups(ct);
            return View(vm);
        }

        var result = await service.CreateAsync(new Pr1CreateRequest(
            vm.PatrolType, vm.PatrolDate, vm.ProjectId, vm.ContractorId, vm.LeaderId, vm.TeamMembers, vm.ImproveGroupId, items), Me, ct);

        await AuditAsync("工安巡查-開單", result.FormId.ToString(), result.Ok ? "建單成功" : result.Error, result.Ok);
        if (!result.Ok)
        {
            ModelState.AddModelError("", result.Error!);
            await FillCreateLookups(ct);
            return View(vm);
        }
        Flash("建單完成，已通知工安組登記桌。");
        return RedirectToAction(nameof(Form), new { id = result.FormId });
    }

    // 表單檢視 + 動作
    [RequireFeature(Features.Pr1Query, Features.Pr1Create, Features.Pr1SafetyReview, Features.Pr1HandlerProcess, Features.Pr1Verify, Features.Pr1Approve)]
    public async Task<IActionResult> Form(long id, CancellationToken ct)
    {
        var form = await repo.GetAsync(id, ct);
        if (form is null) return NotFound();

        // 待辦收件人開啟即承接。
        if (form.PendingRecipientId == Me.EmployeeId && form.CurrentHolderId != Me.EmployeeId)
        {
            await service.OpenAsync(id, Me, ct);
            form = await repo.GetAsync(id, ct);
        }

        var vm = new Pr1FormViewModel
        {
            Form = form!,
            Project = await refs.GetProjectAsync(form!.ProjectId, ct),
            Contractor = await refs.GetContractorAsync(form.ContractorId, ct),
            History = await repo.GetHistoryAsync(id, ct),
            CanAct = form.Status == FormStatus.InProgress && form.CurrentHolderId == Me.EmployeeId && form.PendingRecipientId == Me.EmployeeId,
            Allowed = SafetyWorkflow.AllowedActions(form.Step),
            Groups = await org.GetGroupsAsync(ct),
        };
        return View(vm);
    }

    [HttpPost]
    public async Task<IActionResult> Act(Pr1ActRequest req, CancellationToken ct)
    {
        var result = await service.ActAsync(new Pr1ActionRequest(
            req.FormId, req.Action, req.Comment, req.AssigneeId, req.InformGroupId, req.ImproveDeadline,
            req.IsJump, req.IsProxy, req.AgreeClose), Me, ct);

        await AuditAsync($"工安巡查-{req.Action}", req.FormId.ToString(), result.Ok ? "成功" : result.Error, result.Ok);
        Flash(result.Ok ? "已完成。" : result.Error!, result.Ok ? "success" : "danger");
        return RedirectToAction(nameof(Form), new { id = req.FormId });
    }

    // ----- 逐項 C4 改善結果 / C5 查證結果 -----
    [RequireFeature(Features.Pr1HandlerProcess, Features.Pr1Verify), HttpPost]
    public async Task<IActionResult> SaveItemResults(long formId, List<Pr1ItemResultInput> items, CancellationToken ct)
    {
        var results = (items ?? [])
            .Select(i => new Pr1ItemResult(i.ItemId, i.Improved, i.NotImprovedReason?.Trim(), i.ImprovedDate, i.Verified, i.VerifyReason?.Trim()))
            .ToList();
        var result = await service.SaveItemResultsAsync(formId, results, Me, ct);
        await AuditAsync("工安巡查-儲存改善/查證結果", formId.ToString(), result.Ok ? "成功" : result.Error, result.Ok);
        Flash(result.Ok ? "已儲存。" : result.Error!, result.Ok ? "success" : "danger");
        return RedirectToAction(nameof(Form), new { id = formId });
    }

    // ----- 項目照片（缺失現況 / 改善後）-----
    [RequireFeature(Features.Pr1SafetyReview, Features.Pr1HandlerProcess, Features.Pr1Verify), HttpPost]
    [RequestSizeLimit(20_000_000)]
    public async Task<IActionResult> UploadPhoto(long formId, long itemId, string category, IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            Flash("請選擇要上傳的圖片。", "danger");
            return RedirectToAction(nameof(Form), new { id = formId });
        }
        await using var stream = file.OpenReadStream();
        var result = await service.AddPhotoAsync(formId, itemId, category, file.FileName, file.Length, stream, Me, ct);
        await AuditAsync("工安巡查-上傳照片", $"{formId}/{itemId}", result.Ok ? "成功" : result.Error, result.Ok);
        Flash(result.Ok ? "照片已上傳。" : result.Error!, result.Ok ? "success" : "danger");
        return RedirectToAction(nameof(Form), new { id = formId });
    }

    // 供檔：檢視權限由 RequireFeature 把關（可檢視表單者即可看照片）。id = AttachmentId。
    [RequireFeature(Features.Pr1Query, Features.Pr1Create, Features.Pr1SafetyReview, Features.Pr1HandlerProcess, Features.Pr1Verify, Features.Pr1Approve)]
    public async Task<IActionResult> Photo(long id, CancellationToken ct)
    {
        var photo = await service.OpenPhotoAsync(id, ct);
        if (photo is null) return NotFound();
        return File(photo.Value.Stream, photo.Value.ContentType);
    }

    [RequireFeature(Features.Pr1SafetyReview, Features.Pr1HandlerProcess, Features.Pr1Verify), HttpPost]
    public async Task<IActionResult> DeletePhoto(long formId, long attachmentId, CancellationToken ct)
    {
        var result = await service.DeletePhotoAsync(attachmentId, Me, ct);
        await AuditAsync("工安巡查-刪除照片", $"{formId}/{attachmentId}", result.Ok ? "成功" : result.Error, result.Ok);
        Flash(result.Ok ? "照片已刪除。" : result.Error!, result.Ok ? "success" : "danger");
        return RedirectToAction(nameof(Form), new { id = formId });
    }

    // ----- 下拉輔助 (AJAX) -----
    [RequireFeature(Features.Pr1Create)]
    public async Task<IActionResult> Contractors(int projectId, CancellationToken ct) =>
        Json((await refs.GetContractorsAsync(projectId, true, ct)).Select(c => new { c.ContractorId, c.Name }));

    private async Task FillCreateLookups(CancellationToken ct)
    {
        ViewBag.PatrolTypes = PatrolType.Names.Select(p => new SelectListItem($"{p.Key} {p.Value}", p.Key)).ToList();
        ViewBag.Projects = await refs.GetProjectsAsync(true, ct);
        ViewBag.Contractors = await refs.GetContractorsAsync(null, true, ct);
        ViewBag.Groups = await org.GetGroupsAsync(ct);
        // 巡查領隊：Rank 1/2 (正副主管)。
        ViewBag.Leaders = (await org.FindEmployeesAsync(new EmployeeQuery(), ct))
            .Where(e => e.RankId is Ranks.Director or Ranks.DeputyDirector or Ranks.Manager).ToList();
        ViewBag.Hazards = await refs.GetHazardsAsync(ct);
        ViewBag.DeficiencyTypes = await refs.GetDeficiencyTypesAsync(null, ct);
        ViewBag.DeficiencyItems = await refs.GetDeficiencyItemsAsync(null, ct);
    }
}

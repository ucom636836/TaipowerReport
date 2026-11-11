using System.Text;
using Taipower.Safety.Core.Org;
using Taipower.Safety.Core.Reference;

namespace Taipower.Safety.Core.Forms.Pr3;

/// <summary>組出 Pr3 通知信。連結指向表單頁，未登入者走正常登入 + ReturnUrl。</summary>
public sealed class Pr3EmailComposer(IReferenceRepository refs)
{
    public required Func<string> PublicBaseUrl { get; init; }

    public async Task<Common.EmailMessage> ComposeAsync(Pr3Form form, Employee recipient, string actionLabel, CancellationToken ct = default)
    {
        var project = await refs.GetProjectAsync(form.ProjectId, ct);
        var contractor = await refs.GetContractorAsync(form.ContractorId, ct);
        var baseUrl = PublicBaseUrl().TrimEnd('/');
        var link = $"{baseUrl}/Pr3/Form/{form.FormId}";

        var body = new StringBuilder();
        body.Append($"<p>您有一張各級主管走動管理紀錄暨追蹤表待處理（{actionLabel}）。</p>");
        body.Append("<ul>");
        body.Append($"<li>單號：{form.DisplayNumber}</li>");
        body.Append($"<li>工程名稱：{project?.Name}</li>");
        body.Append($"<li>承攬商：{contractor?.Name}</li>");
        body.Append($"<li>巡查日期：{form.InspectDate:yyyy-MM-dd}</li>");
        if (form.ImproveDeadline is not null) body.Append($"<li>限期改善日：{form.ImproveDeadline:yyyy-MM-dd}</li>");
        body.Append("</ul>");
        body.Append($"<p><a href=\"{link}\">按此處理</a>（需登入系統）</p>");

        return new Common.EmailMessage(
            To: [recipient.Email ?? ""],
            Cc: [],
            Subject: $"[走動管理] {form.DisplayNumber} 待處理 - {actionLabel}",
            HtmlBody: body.ToString());
    }
}

using System.Text;
using Taipower.Safety.Core.Org;
using Taipower.Safety.Core.Reference;

namespace Taipower.Safety.Core.Forms.Pr4;

/// <summary>組出 Pr4 通知信。連結指向表單頁，未登入者走正常登入 + ReturnUrl。</summary>
public sealed class Pr4EmailComposer(IReferenceRepository refs)
{
    public required Func<string> PublicBaseUrl { get; init; }

    public async Task<Common.EmailMessage> ComposeAsync(Pr4Form form, Employee recipient, string actionLabel, CancellationToken ct = default)
    {
        var project = await refs.GetProjectAsync(form.ProjectId, ct);
        var contractor = await refs.GetContractorAsync(form.ContractorId, ct);
        var baseUrl = PublicBaseUrl().TrimEnd('/');
        var link = $"{baseUrl}/Pr4/Form/{form.FormId}";

        var body = new StringBuilder();
        body.Append($"<p>您有一張承攬商工作場所工安抽查紀錄表待處理（{actionLabel}）。</p>");
        body.Append("<ul>");
        body.Append($"<li>單號：{form.DisplayNumber}</li>");
        body.Append($"<li>工程名稱：{project?.Name}</li>");
        body.Append($"<li>承攬商：{contractor?.Name}</li>");
        body.Append($"<li>抽查日期：{form.SpotCheckDate:yyyy-MM-dd}</li>");
        if (form.ImproveDeadline is not null) body.Append($"<li>限期改善日：{form.ImproveDeadline:yyyy-MM-dd}</li>");
        body.Append("</ul>");
        body.Append($"<p><a href=\"{link}\">按此處理</a>（需登入系統）</p>");

        return new Common.EmailMessage(
            To: [recipient.Email ?? ""],
            Cc: [],
            Subject: $"[工安抽查] {form.DisplayNumber} 待處理 - {actionLabel}",
            HtmlBody: body.ToString());
    }
}

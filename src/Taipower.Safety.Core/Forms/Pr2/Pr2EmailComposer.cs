using System.Text;
using Taipower.Safety.Core.Org;
using Taipower.Safety.Core.Reference;

namespace Taipower.Safety.Core.Forms.Pr2;

/// <summary>組出 Pr2 通知信。連結指向表單頁，未登入者走正常登入 + ReturnUrl（單號不出現在網址）。</summary>
public sealed class Pr2EmailComposer(IReferenceRepository refs)
{
    /// <summary>由主機設定的對外基底網址 (e.g. https://safety.example/)。</summary>
    public required Func<string> PublicBaseUrl { get; init; }

    public async Task<Common.EmailMessage> ComposeAsync(Pr2Form form, Employee recipient, string actionLabel, CancellationToken ct = default)
    {
        var project = await refs.GetProjectAsync(form.ProjectId, ct);
        var contractor = await refs.GetContractorAsync(form.ContractorId, ct);
        var baseUrl = PublicBaseUrl().TrimEnd('/');
        var link = $"{baseUrl}/Pr2/Form/{form.FormId}";

        var body = new StringBuilder();
        body.Append($"<p>您有一張工安查核紀錄追蹤表待處理（{actionLabel}）。</p>");
        body.Append("<ul>");
        body.Append($"<li>單號：{form.DisplayNumber}</li>");
        body.Append($"<li>工程名稱：{project?.Name}</li>");
        body.Append($"<li>承攬商：{contractor?.Name}</li>");
        body.Append($"<li>查核日期：{form.CheckDate:yyyy-MM-dd}</li>");
        if (form.ImproveDeadline is not null) body.Append($"<li>改善期限：{form.ImproveDeadline:yyyy-MM-dd}</li>");
        body.Append("</ul>");
        body.Append($"<p><a href=\"{link}\">按此處理</a>（需登入系統）</p>");

        return new Common.EmailMessage(
            To: [recipient.Email ?? ""],
            Cc: [],
            Subject: $"[工安查核] {form.DisplayNumber} 待處理 - {actionLabel}",
            HtmlBody: body.ToString());
    }
}

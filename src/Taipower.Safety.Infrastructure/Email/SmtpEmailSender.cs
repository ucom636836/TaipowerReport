using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using Taipower.Safety.Core.Common;

namespace Taipower.Safety.Infrastructure.Email;

public sealed class EmailOptions
{
    public const string Section = "Email";

    /// <summary>When false, messages are only written to the application log (development).</summary>
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 25;
    public bool UseStartTls { get; set; }
    /// <summary>Credentials come from user-secrets / environment variables, never appsettings in source control.</summary>
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "台電南部施工處工安表單系統";
    /// <summary>Test mode: if set, every message is delivered here instead of the real recipients (legacy "testmail").</summary>
    public string? RedirectAllTo { get; set; }
    /// <summary>Absolute base URL used in links, e.g. https://safety.example/ (legacy MailUrl).</summary>
    public string PublicBaseUrl { get; set; } = "";
}

public sealed class SmtpEmailSender(IOptions<EmailOptions> options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    private readonly EmailOptions _o = options.Value;

    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        var to = message.To.Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var cc = message.Cc.Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var subject = message.Subject;

        if (!string.IsNullOrWhiteSpace(_o.RedirectAllTo))
        {
            subject = $"[TEST → {string.Join(", ", to)}] {subject}";
            to = [_o.RedirectAllTo];
            cc = [];
        }

        if (to.Count == 0)
        {
            logger.LogWarning("E-mail '{Subject}' has no recipients; skipped.", subject);
            return;
        }

        if (!_o.Enabled)
        {
            logger.LogInformation("E-mail disabled. Would send '{Subject}' to {To} (cc {Cc}).", subject, string.Join(", ", to), string.Join(", ", cc));
            return;
        }

        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(_o.FromName, _o.FromAddress));
        mime.To.AddRange(to.Select(MailboxAddress.Parse));
        mime.Cc.AddRange(cc.Select(MailboxAddress.Parse));
        mime.Subject = subject;
        mime.Body = new BodyBuilder { HtmlBody = message.HtmlBody }.ToMessageBody();

        using var client = new SmtpClient();
        await client.ConnectAsync(_o.Host, _o.Port, _o.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto, ct);
        if (!string.IsNullOrEmpty(_o.UserName))
            await client.AuthenticateAsync(_o.UserName, _o.Password ?? "", ct);
        await client.SendAsync(mime, ct);
        await client.DisconnectAsync(true, ct);
    }
}

using Microsoft.AspNetCore.Mvc;
using Taipower.Safety.Core.Common;

namespace Taipower.Safety.Web.Infrastructure;

/// <summary>Base controller: current user + audit helper. Authorization is global (see Program.cs).</summary>
public abstract class AppController : Controller
{
    private CurrentUser? _me;
    protected CurrentUser Me => _me ??= User.ToCurrentUser();

    protected Task AuditAsync(string action, string? target, string? message, bool success = true)
    {
        var audit = HttpContext.RequestServices.GetRequiredService<IAuditLog>();
        var authenticated = User.Identity?.IsAuthenticated == true;
        return audit.WriteAsync(new AuditEntry(
            authenticated ? Me.EmployeeId : null,
            authenticated ? Me.Name : null,
            action, target, message, success,
            HttpContext.Connection.RemoteIpAddress?.ToString()), HttpContext.RequestAborted);
    }

    protected void Flash(string message, string kind = "success") => TempData["flash." + kind] = message;
}

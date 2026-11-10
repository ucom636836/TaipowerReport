using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taipower.Safety.Core.Forms.Pr1;
using Taipower.Safety.Web.Infrastructure;

namespace Taipower.Safety.Web.Controllers;

public sealed class HomeController(IPr1Repository pr1) : AppController
{
    public async Task<IActionResult> Index(CancellationToken ct)
        => View(await pr1.GetInboxAsync(Me.EmployeeId, ct));

    [AllowAnonymous, ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error() => View(model: Activity.Current?.Id ?? HttpContext.TraceIdentifier);
}

using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taipower.Safety.Core.Common;
using Taipower.Safety.Core.Security;
using Taipower.Safety.Web.Infrastructure;

namespace Taipower.Safety.Web.Controllers;

public sealed class LoginInput
{
    [Required(ErrorMessage = "請輸入帳號")] public string Account { get; set; } = "";
    [Required(ErrorMessage = "請輸入密碼")] public string Password { get; set; } = "";
    public string? ReturnUrl { get; set; }
}

public sealed class ChangePasswordInput
{
    [Required(ErrorMessage = "請輸入目前密碼")] public string CurrentPassword { get; set; } = "";

    [Required(ErrorMessage = "請輸入新密碼")]
    [MinLength(8, ErrorMessage = "新密碼至少 8 碼")]
    public string NewPassword { get; set; } = "";

    [Compare(nameof(NewPassword), ErrorMessage = "兩次輸入的新密碼不一致")]
    public string ConfirmPassword { get; set; } = "";
}

public sealed class AccountController(
    IAccountRepository accounts,
    IOrgRepository org,
    IPermissionRepository permissions) : AppController
{
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    [AllowAnonymous, HttpGet]
    public IActionResult Login(string? returnUrl) => View(new LoginInput { ReturnUrl = returnUrl });

    [AllowAnonymous, HttpPost]
    public async Task<IActionResult> Login(LoginInput input, CancellationToken ct)
    {
        if (!ModelState.IsValid) return View(input);

        var account = await accounts.FindAsync(input.Account.Trim(), ct);
        // Same message for unknown account / wrong password to avoid account enumeration.
        const string invalid = "帳號或密碼錯誤";

        if (account is null)
        {
            await AuditAsync("登入", input.Account, "帳號不存在", success: false);
            ModelState.AddModelError("", invalid);
            return View(input);
        }
        if (account.LockoutUntil > DateTime.Now)
        {
            await AuditAsync("登入", input.Account, "帳號鎖定中", success: false);
            ModelState.AddModelError("", $"登入失敗次數過多，請於 {account.LockoutUntil:HH:mm} 後再試");
            return View(input);
        }
        if (!account.IsActive)
        {
            await AuditAsync("登入", input.Account, "帳號未啟用", success: false);
            ModelState.AddModelError("", "帳號未啟用，請聯絡系統管理員");
            return View(input);
        }

        var verification = PasswordHasher.Verify(input.Password, account.PasswordHash);
        if (verification == PasswordVerification.Failed)
        {
            await accounts.RecordLoginFailureAsync(account.Account, MaxFailedAttempts, LockoutDuration, ct);
            await AuditAsync("登入", input.Account, "密碼錯誤", success: false);
            ModelState.AddModelError("", invalid);
            return View(input);
        }

        var newHash = verification == PasswordVerification.SuccessRehashNeeded ? PasswordHasher.Hash(input.Password) : null;
        await accounts.RecordLoginSuccessAsync(account.Account, newHash, ct);

        var emp = await org.GetEmployeeAsync(account.EmployeeId, ct)
                  ?? throw new InvalidOperationException($"Employee {account.EmployeeId} missing");
        var features = emp.RoleId is int roleId ? await permissions.GetRoleFeaturesAsync(roleId, ct) : new HashSet<int>();

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, account.Account),
            new(ClaimTypes.Name, emp.Name),
            new(AppClaims.EmployeeId, emp.EmployeeId),
            new(AppClaims.Title, emp.Title),
        };
        if (emp.GroupId is not null) claims.Add(new(AppClaims.GroupId, emp.GroupId));
        if (emp.SectionCode is not null) claims.Add(new(AppClaims.SectionCode, emp.SectionCode));
        if (emp.RankId is not null) claims.Add(new(AppClaims.RankId, emp.RankId.Value.ToString()));
        if (emp.RoleId is not null) claims.Add(new(AppClaims.RoleId, emp.RoleId.Value.ToString()));
        claims.AddRange(features.Select(f => new Claim(AppClaims.Feature, f.ToString())));

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));

        await HttpContext.RequestServices.GetRequiredService<IAuditLog>().WriteAsync(
            new AuditEntry(emp.EmployeeId, emp.Name, "登入", account.Account, "登入成功", true, HttpContext.Connection.RemoteIpAddress?.ToString()), ct);

        return Url.IsLocalUrl(input.ReturnUrl) ? LocalRedirect(input.ReturnUrl!) : RedirectToAction("Index", "Home");
    }

    [HttpPost]
    public async Task<IActionResult> Logout()
    {
        await AuditAsync("登出", null, null);
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }

    [AllowAnonymous]
    public IActionResult Denied() => View();

    [HttpGet]
    public IActionResult ChangePassword() => View(new ChangePasswordInput());

    [HttpPost]
    public async Task<IActionResult> ChangePassword(ChangePasswordInput input, CancellationToken ct)
    {
        if (!ModelState.IsValid) return View(input);
        var accountName = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var account = await accounts.FindAsync(accountName, ct);
        if (account is null || PasswordHasher.Verify(input.CurrentPassword, account.PasswordHash) == PasswordVerification.Failed)
        {
            ModelState.AddModelError(nameof(input.CurrentPassword), "目前密碼不正確");
            return View(input);
        }
        await accounts.SetPasswordAsync(accountName, PasswordHasher.Hash(input.NewPassword), ct);
        await AuditAsync("變更密碼", accountName, null);
        Flash("密碼已變更");
        return RedirectToAction("Index", "Home");
    }
}

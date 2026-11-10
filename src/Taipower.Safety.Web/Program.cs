using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Authorization;
using Taipower.Safety.Core.Common;
using Taipower.Safety.Core.Reference;
using Taipower.Safety.Infrastructure;
using Taipower.Safety.Web.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSafetyInfrastructure(builder.Configuration);
builder.Services.AddSingleton<IClock, SystemClock>();

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/Account/Login";
        o.LogoutPath = "/Account/Logout";
        o.AccessDeniedPath = "/Account/Denied";
        o.Cookie.Name = "tpsafety.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan = TimeSpan.FromMinutes(60);
        o.SlidingExpiration = true;
    });

builder.Services.AddSingleton<IAuthorizationPolicyProvider, FeaturePolicyProvider>();
builder.Services.AddSingleton<IAuthorizationHandler, FeatureHandler>();
builder.Services.AddAuthorization();

builder.Services.AddControllersWithViews(o =>
{
    // Every endpoint requires login unless explicitly [AllowAnonymous] (legacy app had no checks at all).
    o.Filters.Add(new AuthorizeFilter());
    // Every unsafe HTTP method requires an antiforgery token.
    o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
});

builder.Services.AddAntiforgery(o => o.HeaderName = "RequestVerificationToken");

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();

public partial class Program;

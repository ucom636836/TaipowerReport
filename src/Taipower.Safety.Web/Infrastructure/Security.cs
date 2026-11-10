using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Taipower.Safety.Core.Common;

namespace Taipower.Safety.Web.Infrastructure;

public static class AppClaims
{
    public const string EmployeeId = "emp";
    public const string GroupId = "grp";
    public const string SectionCode = "sec";
    public const string RankId = "rank";
    public const string RoleId = "role";
    public const string Feature = "feat";
    public const string Title = "title";
}

/// <summary>Requires the user's role to be granted the given feature (RoleFeature table).</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequireFeatureAttribute(params int[] anyOf) : AuthorizeAttribute(PolicyPrefix + string.Join(',', anyOf))
{
    public const string PolicyPrefix = "feature:";
}

public sealed class FeatureRequirement(IReadOnlyList<int> anyOf) : IAuthorizationRequirement
{
    public IReadOnlyList<int> AnyOf { get; } = anyOf;
}

public sealed class FeatureHandler : AuthorizationHandler<FeatureRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, FeatureRequirement requirement)
    {
        var granted = context.User.FindAll(AppClaims.Feature).Select(c => c.Value).ToHashSet();
        if (requirement.AnyOf.Any(f => granted.Contains(f.ToString())))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

/// <summary>Builds "feature:101,102" policies on demand.</summary>
public sealed class FeaturePolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith(RequireFeatureAttribute.PolicyPrefix, StringComparison.Ordinal))
        {
            var ids = policyName[RequireFeatureAttribute.PolicyPrefix.Length..]
                .Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
            return new AuthorizationPolicyBuilder().RequireAuthenticatedUser().AddRequirements(new FeatureRequirement(ids)).Build();
        }
        return await base.GetPolicyAsync(policyName);
    }
}

public static class ClaimsPrincipalExtensions
{
    public static CurrentUser ToCurrentUser(this ClaimsPrincipal p)
    {
        static int? Int(string? s) => int.TryParse(s, out var v) ? v : null;
        return new CurrentUser(
            p.FindFirstValue(AppClaims.EmployeeId) ?? throw new InvalidOperationException("Not authenticated"),
            p.FindFirstValue(ClaimTypes.Name) ?? "",
            p.FindFirstValue(AppClaims.GroupId),
            p.FindFirstValue(AppClaims.SectionCode),
            Int(p.FindFirstValue(AppClaims.RankId)),
            Int(p.FindFirstValue(AppClaims.RoleId)),
            p.FindAll(AppClaims.Feature).Select(c => int.Parse(c.Value)).ToHashSet());
    }

    public static bool HasFeature(this ClaimsPrincipal p, int featureId) =>
        p.HasClaim(AppClaims.Feature, featureId.ToString());
}

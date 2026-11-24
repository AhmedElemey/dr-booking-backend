using Microsoft.AspNetCore.Mvc.Filters;

using Moonlight.Api.Common;

namespace Moonlight.Api.Features.Flags;

/// <summary>Staff and doctor tokens carry a clinicId claim: every feature check in their request answers for that clinic.</summary>
public sealed class ClinicFeatureScopeFilter(FeatureService features, CurrentUser current) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        using var scope = features.UseClinic(current.ClinicId);
        await next();
    }
}

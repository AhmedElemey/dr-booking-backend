using Microsoft.AspNetCore.Mvc.Filters;

using Moonlight.Api.Common;

namespace Moonlight.Api.Features.Flags;

/// <summary>Answers 403 FEATURE_DISABLED when the feature is switched off for this clinic.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireFeatureAttribute(string feature) : Attribute, IFilterFactory
{
    public bool IsReusable => false;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider) =>
        new Filter(feature, serviceProvider.GetRequiredService<FeatureService>(), serviceProvider.GetRequiredService<CurrentUser>());

    private sealed class Filter(string feature, FeatureService features, CurrentUser current) : IActionFilter
    {
        // Staff and doctor tokens carry a clinicId claim, so their checks are per clinic; patients and
        // anonymous callers fall back to the deployment-wide value.
        public void OnActionExecuting(ActionExecutingContext context)
        {
            if (current.ClinicId is { } clinicId)
            {
                features.Require(feature, clinicId);
            }
            else
            {
                features.Require(feature);
            }
        }

        public void OnActionExecuted(ActionExecutedContext context)
        {
        }
    }
}

/// <summary>With guestBrowsing off, anonymous callers get 401 so clients send them to sign in.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireSignInUnlessGuestBrowsingAttribute : Attribute, IFilterFactory
{
    public bool IsReusable => false;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider) =>
        new Filter(serviceProvider.GetRequiredService<FeatureService>());

    private sealed class Filter(FeatureService features) : IActionFilter
    {
        public void OnActionExecuting(ActionExecutingContext context)
        {
            if (!features.IsEnabled(FeatureKeys.GuestBrowsing) && context.HttpContext.User.Identity?.IsAuthenticated != true)
            {
                throw new ApiException(ErrorCodes.Unauthorized);
            }
        }

        public void OnActionExecuted(ActionExecutedContext context)
        {
        }
    }
}

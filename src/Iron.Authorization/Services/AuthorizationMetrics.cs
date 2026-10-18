using System.Diagnostics.Metrics;

namespace Iron.Authorization.Services;

/// <summary>The <c>authz.denials</c> counter by reason. An alert on <c>reason=unavailable</c> is TF-031 AC4's "alert is raised".</summary>
public static class AuthorizationMetrics
{
    public static readonly Meter Meter = new("Iron.Authorization");

    public static readonly Counter<long> Denials = Meter.CreateCounter<long>("authz.denials", description: "Permission checks that ended in a denial, by reason.");
}

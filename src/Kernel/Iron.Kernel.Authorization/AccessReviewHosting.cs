using Iron.Kernel.Authorization.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Authorization;

public static class AccessReviewHosting
{
    /// <summary>
    /// The daily escalation pass of access reviews (TF-038 AC3), for the unit's worker host only: the API hosts register
    /// the same modules, and one pass per unit is enough.
    /// </summary>
    public static IServiceCollection AddAccessReviewEscalation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<ReviewEscalationWorker>();
        return services;
    }

    /// <summary>The daily run of the conflicts report (TF-036 AC3), for the unit's worker host only, like the escalation pass.</summary>
    public static IServiceCollection AddConflictReport(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<ConflictReportWorker>();
        return services;
    }
}

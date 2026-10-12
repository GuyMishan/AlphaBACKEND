using Alpha.Modules.Auditing.Features.SecurityEvents;
using Microsoft.AspNetCore.Routing;

namespace Alpha.Modules.Auditing;

public static class AuditingModule
{
    /// <summary>Security-event inspection, attached to the host-owned <c>/api/platform</c> group.</summary>
    public static RouteGroupBuilder MapAuditingPlatformEndpoints(this RouteGroupBuilder platform) =>
        platform.MapAuditingEndpoints();
}

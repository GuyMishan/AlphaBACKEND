using Alpha.Modules.IdentityAccess.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Alpha.Modules.Organizations.Features.EmployerTransfers;

public static class EmployerTransferEndpoints
{
    public static RouteGroupBuilder MapEmployerTransferEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/{organizationId:guid}/employers/{employerId:guid}/transfer", async (
            Guid organizationId, Guid employerId, TransferEmployerRequest request,
            EmployerTransferService transfer, ICurrentUser user, HttpContext http, CancellationToken ct) =>
        {
            if (!user.IsPlatformAdmin) return Results.Forbid();
            var result = await transfer.TransferAsync(
                organizationId, employerId, request.TargetOrganizationId,
                http.TraceIdentifier, ct);
            if (!result.Success)
                return result.Error == "forbidden"
                    ? Results.Forbid()
                    : Results.Conflict(new { error = result.Error, detail = result.Message });
            return Results.Ok(new { employerId, organizationId = request.TargetOrganizationId });
        });

        return group;
    }

    public sealed record TransferEmployerRequest(Guid TargetOrganizationId);
}

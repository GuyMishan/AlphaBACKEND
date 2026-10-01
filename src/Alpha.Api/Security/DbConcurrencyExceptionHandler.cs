using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Security;

public sealed class DbConcurrencyExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not DbUpdateConcurrencyException)
            return false;

        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;
        await httpContext.Response.WriteAsJsonAsync(new
        {
            error = "concurrency_conflict",
            detail = "הנתונים השתנו במקביל על ידי פעולה אחרת. רעננו את המסך ונסו שוב."
        }, cancellationToken);
        return true;
    }
}

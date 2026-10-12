namespace Alpha.Api.Errors;

public static class ErrorHandlingSetup
{
    /// <summary>One writer (<see cref="IProblemDetailsService"/>), one enrichment, one exception handler.</summary>
    public static IServiceCollection AddAlphaErrorHandling(this IServiceCollection services)
    {
        services.AddExceptionHandler<ApiExceptionHandler>();
        services.AddProblemDetails(options => options.CustomizeProblemDetails = ApiErrors.Enrich);
        // Unreadable bodies and parameters throw in every environment so they map to MALFORMED_REQUEST; the framework
        // default throws only in Development and otherwise answers a bare 400.
        services.PostConfigure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        return services;
    }
}

using System.Text.Json;
using Alpha.Api.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Alpha.Api.Tests.Security;

public sealed class DbConcurrencyExceptionHandlerTests
{
    [Fact]
    public async Task Concurrency_conflict_returns_the_unified_contract_without_exception_text()
    {
        var (handler, context) = Create();

        var handled = await handler.TryHandleAsync(context,
            new DbUpdateConcurrencyException("Sensitive database detail must not be returned."), TestContext.Current.CancellationToken);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var payload = await JsonDocument.ParseAsync(context.Response.Body, cancellationToken: TestContext.Current.CancellationToken);
        var root = payload.RootElement;
        Assert.Equal("CONCURRENCY_CONFLICT", root.GetProperty("errorCode").GetString());
        Assert.Equal("concurrency_conflict", root.GetProperty("error").GetString());
        Assert.Equal("concurrency-contract-test", root.GetProperty("traceId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("detail").GetString()));
        Assert.DoesNotContain("Sensitive database detail", root.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unmapped_exceptions_and_started_responses_are_left_to_the_framework()
    {
        var (handler, context) = Create();
        Assert.False(await handler.TryHandleAsync(context, new InvalidOperationException("test"), TestContext.Current.CancellationToken));
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    private static (ApiExceptionHandler Handler, DefaultHttpContext Context) Create()
    {
        var services = new ServiceCollection().AddLogging().AddAlphaErrorHandling().BuildServiceProvider();
        var context = new DefaultHttpContext { TraceIdentifier = "concurrency-contract-test", RequestServices = services };
        context.Response.Body = new MemoryStream();
        return (new ApiExceptionHandler(services.GetRequiredService<IProblemDetailsService>(), NullLogger<ApiExceptionHandler>.Instance), context);
    }
}

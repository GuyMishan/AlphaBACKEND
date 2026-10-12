using Alpha.Domain.Reporting;
using Alpha.Infrastructure.Persistence;
using Alpha.Modules.Reporting.Features.Import;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Alpha.Api.Tests.TestInfrastructure.AlphaApiTestHost;
using static Alpha.Api.Tests.TestInfrastructure.TestData;

namespace Alpha.Api.Tests.Reporting;

/// <summary>The startup normalization of legacy feedback must be bounded, non-fatal and run until one clean pass.</summary>
public sealed class LegacyFeedbackNormalizationTests
{
    [Fact]
    public async Task Unreadable_feedback_is_reported_without_failing_and_completion_is_recorded_only_after_repair()
    {
        await RunAsync(async (factory, _, ct) =>
        {
            var tenant = await SeedTenantAsync(factory, "F");
            var report = new ManualReport(tenant.Organization.Id, tenant.Employer.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 5));
            var unreadable = Feedback(tenant, report.Id, "alpha:v1:not-base64:x:y", "h1");
            var readable = Feedback(tenant, report.Id, "{\"simulated\":true}", "h2");
            await factory.WithDbAsync(async db =>
            {
                // The host already ran the startup step on the empty database; start from "not yet applied".
                await db.Database.ExecuteSqlRawAsync($"DELETE FROM identity.data_fixes WHERE \"Key\" = '{LegacyFeedbackNormalization.CompletionKey}'", ct);
                db.AddRange(report, unreadable, readable);
                await db.SaveChangesAsync(ct);
            });

            var first = await RunNormalizationAsync(factory, ct);
            Assert.False(first.Skipped);
            Assert.Equal(2, first.Processed);
            Assert.Equal(new[] { unreadable.Id }, first.FailedFeedbackIds);
            Assert.False(await AppliedAsync(factory, ct));

            await factory.WithDbAsync(async db =>
            {
                const string repaired = "{\"simulated\":true}";
                await db.Database.ExecuteSqlAsync(
                    $"UPDATE reporting.employer_interface_feedback SET \"RawXml\" = {repaired} WHERE \"Id\" = {unreadable.Id}", ct);
            });
            var second = await RunNormalizationAsync(factory, ct);
            Assert.Empty(second.FailedFeedbackIds);
            Assert.True(await AppliedAsync(factory, ct));

            Assert.True((await RunNormalizationAsync(factory, ct)).Skipped);
        });
    }

    private static EmployerInterfaceFeedback Feedback(Tenant tenant, Guid reportId, string rawXml, string hash)
    {
        var feedback = new EmployerInterfaceFeedback(tenant.Organization.Id, tenant.Employer.Id,
            EmployerInterfaceDocumentType.SummaryFeedback, "006", $"{hash}.xml", hash, rawXml);
        feedback.Correlate(reportId, null);
        return feedback;
    }

    private static async Task<LegacyFeedbackNormalization.Result> RunNormalizationAsync(Factory factory, CancellationToken ct)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<LegacyFeedbackNormalization>().RunAsync(ct);
    }

    private static async Task<bool> AppliedAsync(Factory factory, CancellationToken ct)
    {
        var applied = false;
        await factory.WithDbAsync(async db => applied = await DataFixMarkers.IsAppliedAsync(db, LegacyFeedbackNormalization.CompletionKey, ct));
        return applied;
    }
}

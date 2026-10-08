using Alpha.Api.Services;
using Alpha.Domain.Reporting;
using Xunit;

namespace Alpha.Api.Tests;

public sealed class ReportTransmissionFeedbackSelectionTests
{
    [Fact]
    public void Each_manufacturer_keeps_its_own_latest_transmission_feedback()
    {
        var employer = Guid.NewGuid();
        var organization = Guid.NewGuid();
        var report = Guid.NewGuid();
        var directFirst = new ReportTransmission(report, organization, employer, "direct-a", 1);
        directFirst.ConfigureRoute("direct-a", [Guid.NewGuid()]);
        var clearing = new ReportTransmission(report, organization, employer, "clearinghouse", 2);
        clearing.ConfigureRoute("clearinghouse", [Guid.NewGuid()]);
        var directRetry = new ReportTransmission(report, organization, employer, "direct-a", 3);
        directRetry.ConfigureRoute("direct-a", [Guid.NewGuid()]);

        var latest = ReportTransmissionFeedbackSelection.LatestAttemptIds(
            [directFirst, clearing, directRetry]);

        Assert.DoesNotContain(directFirst.Id, latest);
        Assert.Contains(clearing.Id, latest);
        Assert.Contains(directRetry.Id, latest);
        Assert.True(ReportTransmissionFeedbackSelection.IsActive(clearing.Id, latest, true));
        Assert.False(ReportTransmissionFeedbackSelection.IsActive(directFirst.Id, latest, true));
    }

    [Fact]
    public void Legacy_import_without_any_transmission_keeps_unlinked_feedback()
    {
        var latest = ReportTransmissionFeedbackSelection.LatestAttemptIds([]);
        Assert.True(ReportTransmissionFeedbackSelection.IsActive(null, latest, false));
        Assert.False(ReportTransmissionFeedbackSelection.IsActive(Guid.NewGuid(), latest, false));
    }

    [Fact]
    public void Unlinked_feedback_must_not_be_counted_when_recipient_attempts_exist()
    {
        var report = Guid.NewGuid();
        var tx = new ReportTransmission(report, Guid.NewGuid(), Guid.NewGuid(), "clearing", 1);
        var latest = ReportTransmissionFeedbackSelection.LatestAttemptIds([tx]);
        Assert.False(ReportTransmissionFeedbackSelection.IsActive(null, latest, true));
    }

    [Fact]
    public void Prepared_batch_is_not_dispatched_before_explicit_begin()
    {
        var tx = new ReportTransmission(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "clearing", 1);
        tx.ConfigureRoute("clearing", [Guid.NewGuid()]);
        tx.Prepare("hash", "sample.EMPONG.006.TST", [1, 2, 3]);

        Assert.Equal(ReportTransmissionStatus.Pending, tx.Status);
        Assert.Null(tx.StartedAt);
        Assert.Throws<InvalidOperationException>(() => tx.ConfigureRoute("other", [Guid.NewGuid()]));

        tx.BeginDispatch();
        Assert.Equal(ReportTransmissionStatus.Sending, tx.Status);
        Assert.NotNull(tx.StartedAt);
        Assert.Throws<InvalidOperationException>(() => tx.BeginDispatch());
    }
}

using Alpha.Domain.Reporting;
using Xunit;

namespace Alpha.Api.Tests;

public sealed class ReportTransmissionDomainStressTests
{
    [Fact]
    public void Start_copies_payload_bytes_so_saved_evidence_cannot_be_changed_by_caller()
    {
        var transmission = NewTransmission();
        var payload = new byte[] { 1, 2, 3, 4 };

        transmission.Start("hash-1", "PAYLOAD.DAT", payload);
        payload[0] = 99;

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, transmission.Payload);
        Assert.Equal(ReportTransmissionStatus.Sending, transmission.Status);
        Assert.NotNull(transmission.StartedAt);
    }

    [Fact]
    public void Accepted_transmission_records_external_id_and_sent_timestamp()
    {
        var transmission = NewTransmission();
        transmission.Start("hash-1", "PAYLOAD.DAT", new byte[] { 1 });

        transmission.Complete(ReportTransmissionStatus.Accepted, "EXT-123", "ok", null);

        Assert.Equal(ReportTransmissionStatus.Accepted, transmission.Status);
        Assert.Equal("EXT-123", transmission.ExternalId);
        Assert.NotNull(transmission.SentAt);
        Assert.NotNull(transmission.CompletedAt);
    }

    [Fact]
    public void Explicit_rejection_completes_attempt_without_claiming_it_was_sent()
    {
        var transmission = NewTransmission();
        transmission.Start("hash-1", "PAYLOAD.DAT", new byte[] { 1 });

        transmission.Complete(ReportTransmissionStatus.Rejected, null, null, "declined");

        Assert.Equal(ReportTransmissionStatus.Rejected, transmission.Status);
        Assert.Null(transmission.SentAt);
        Assert.NotNull(transmission.CompletedAt);
        Assert.Equal("declined", transmission.ErrorMessage);
    }

    [Fact]
    public void Explicit_provider_rejection_can_return_report_to_editable_error_then_revalidate()
    {
        var report = NewValidatedReport();
        report.MarkTransmissionStarted();
        report.MarkTransmissionError("provider rejected");

        Assert.Equal(ManualReportStatus.Error, report.Status);
        Assert.True(report.IsEditable);

        report.MarkReadyForValidation();
        report.MarkValidated();

        Assert.Equal(ManualReportStatus.Validated, report.Status);
        Assert.False(report.IsEditable);
    }

    [Fact]
    public void Processing_report_cannot_be_claimed_for_a_second_send()
    {
        var report = NewValidatedReport();
        report.MarkTransmissionStarted();

        Assert.Throws<InvalidOperationException>(() => report.MarkTransmissionStarted());
        Assert.Equal(ManualReportStatus.Processing, report.Status);
    }

    [Theory]
    [InlineData(ReportTransmissionStatus.Pending)]
    [InlineData(ReportTransmissionStatus.Sending)]
    public void Transmission_attempt_cannot_be_completed_into_non_terminal_status(ReportTransmissionStatus status)
    {
        var transmission = NewTransmission();
        transmission.Start("hash-1", "PAYLOAD.DAT", new byte[] { 1 });

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            transmission.Complete(status, null, null, null));
    }

    private static ReportTransmission NewTransmission() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "provider", 1);

    private static ManualReport NewValidatedReport()
    {
        var report = new ManualReport(Guid.NewGuid(), Guid.NewGuid(),
            new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 9));
        report.MarkReadyForValidation();
        report.MarkValidated();
        return report;
    }
}

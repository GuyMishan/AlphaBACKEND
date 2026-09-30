using Microsoft.EntityFrameworkCore;

namespace Alpha.Infrastructure.Persistence;

public static class PaymentConfirmationSchemaInitializer
{
    public static Task EnsureUpdatedAsync(AlphaDbContext db, CancellationToken ct = default) =>
        db.Database.ExecuteSqlRawAsync("""
CREATE TABLE IF NOT EXISTS reporting.payment_confirmations (
    "Id" uuid PRIMARY KEY,
    "ReportId" uuid NOT NULL REFERENCES reporting.manual_reports("Id") ON DELETE RESTRICT,
    "ReportProductId" uuid NOT NULL REFERENCES reporting.manual_report_products("Id") ON DELETE RESTRICT,
    "StoragePath" varchar(400) NOT NULL UNIQUE,
    "OriginalFileName" varchar(260) NOT NULL,
    "ContentType" varchar(80) NOT NULL,
    "SizeBytes" bigint NOT NULL,
    "Sha256" varchar(64) NOT NULL,
    "UploadedByUserId" uuid NOT NULL,
    "CreatedAt" timestamptz NOT NULL,
    "UpdatedAt" timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS "IX_payment_confirmations_ReportId_ReportProductId_CreatedAt"
    ON reporting.payment_confirmations ("ReportId", "ReportProductId", "CreatedAt");
""", ct);
}

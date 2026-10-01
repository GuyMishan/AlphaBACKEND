using Microsoft.EntityFrameworkCore;

namespace Alpha.Infrastructure.Persistence;

public static class ReportFeedbackOperationsSchemaInitializer
{
    public static async Task EnsureUpdatedAsync(AlphaDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS reporting.employer_interface_transfer_feedback (
                "Id" uuid PRIMARY KEY,
                "FeedbackId" uuid NOT NULL,
                "ReportId" uuid NOT NULL,
                "TransferIdentifier" varchar(100) NOT NULL,
                "ClearingIdentifier" varchar(100) NOT NULL DEFAULT '',
                "ReportedDepositAmount" numeric(18,2) NOT NULL DEFAULT 0,
                "ActualReceivedAmount" numeric(18,2) NOT NULL DEFAULT 0,
                "AllocatedAmount" numeric(18,2) NOT NULL DEFAULT 0,
                "InTransitAmount" numeric(18,2) NOT NULL DEFAULT 0,
                "ProactiveRefundAmount" numeric(18,2) NULL,
                "EmployerAccountRefundAmount" numeric(18,2) NULL,
                "MoneyTreatmentStatus" integer NULL,
                "StatusDetail" varchar(200) NOT NULL DEFAULT '',
                "PaymentReference" varchar(100) NOT NULL DEFAULT '',
                "ValueDate" date NULL,
                "TrustAccountValueDate" date NULL,
                "CorrectnessTimestamp" varchar(32) NOT NULL DEFAULT '',
                "ReceivedAt" timestamptz NOT NULL,
                "CreatedAt" timestamptz NOT NULL,
                "UpdatedAt" timestamptz NOT NULL,
                CONSTRAINT "FK_transfer_feedback_feedback" FOREIGN KEY ("FeedbackId")
                    REFERENCES reporting.employer_interface_feedback("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_transfer_feedback_report" FOREIGN KEY ("ReportId")
                    REFERENCES reporting.manual_reports("Id") ON DELETE RESTRICT
            );
            CREATE INDEX IF NOT EXISTS "IX_transfer_feedback_ReportId_TransferIdentifier_ReceivedAt"
                ON reporting.employer_interface_transfer_feedback ("ReportId", "TransferIdentifier", "ReceivedAt");
            CREATE INDEX IF NOT EXISTS "IX_transfer_feedback_FeedbackId"
                ON reporting.employer_interface_transfer_feedback ("FeedbackId");
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_transfer_feedback_FeedbackId_TransferIdentifier"
                ON reporting.employer_interface_transfer_feedback ("FeedbackId", "TransferIdentifier");

            CREATE TABLE IF NOT EXISTS reporting.employer_interface_contribution_feedback (
                "Id" uuid PRIMARY KEY,
                "FeedbackId" uuid NOT NULL,
                "ReportId" uuid NOT NULL,
                "ReportProductId" uuid NOT NULL,
                "ContributionId" uuid NOT NULL,
                "RecordIdentifier" varchar(36) NOT NULL,
                "Sequence" integer NOT NULL DEFAULT 0,
                "IntakeStatus" integer NULL,
                "ErrorCode" integer NULL,
                "ErrorDescription" varchar(2000) NOT NULL DEFAULT '',
                "ErrorAmount" numeric(18,2) NULL,
                "ErrorDate" date NULL,
                "ContributionTypeCode" integer NULL,
                "CalculatedSalary" numeric(18,2) NULL,
                "SalaryMonth" date NULL,
                "PolicyNumber" varchar(100) NOT NULL DEFAULT '',
                "ContributionRate" numeric(9,4) NULL,
                "ContributionAmount" numeric(18,2) NULL,
                "SourceFileName" varchar(260) NOT NULL DEFAULT '',
                "ReceivedAt" timestamptz NOT NULL,
                "CreatedAt" timestamptz NOT NULL,
                "UpdatedAt" timestamptz NOT NULL,
                CONSTRAINT "FK_contribution_feedback_feedback" FOREIGN KEY ("FeedbackId")
                    REFERENCES reporting.employer_interface_feedback("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_contribution_feedback_report" FOREIGN KEY ("ReportId")
                    REFERENCES reporting.manual_reports("Id") ON DELETE RESTRICT,
                CONSTRAINT "FK_contribution_feedback_product" FOREIGN KEY ("ReportProductId")
                    REFERENCES reporting.manual_report_products("Id") ON DELETE RESTRICT,
                CONSTRAINT "FK_contribution_feedback_contribution" FOREIGN KEY ("ContributionId")
                    REFERENCES reporting.manual_contributions("Id") ON DELETE RESTRICT
            );
            CREATE INDEX IF NOT EXISTS "IX_contribution_feedback_ReportId_Product_ReceivedAt"
                ON reporting.employer_interface_contribution_feedback ("ReportId", "ReportProductId", "ReceivedAt");
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_contribution_feedback_Feedback_Record_Sequence"
                ON reporting.employer_interface_contribution_feedback ("FeedbackId", "RecordIdentifier", "Sequence");
            CREATE INDEX IF NOT EXISTS "IX_contribution_feedback_ContributionId"
                ON reporting.employer_interface_contribution_feedback ("ContributionId");

            CREATE TABLE IF NOT EXISTS reporting.report_product_treatments (
                "Id" uuid PRIMARY KEY,
                "ReportProductId" uuid NOT NULL,
                "StatusCode" varchar(80) NOT NULL,
                "Note" text NOT NULL DEFAULT '',
                "UpdatedByUserId" uuid NOT NULL,
                "CreatedAt" timestamptz NOT NULL,
                "UpdatedAt" timestamptz NOT NULL,
                CONSTRAINT "FK_product_treatment_product" FOREIGN KEY ("ReportProductId")
                    REFERENCES reporting.manual_report_products("Id") ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_product_treatment_ReportProductId"
                ON reporting.report_product_treatments ("ReportProductId");
            CREATE INDEX IF NOT EXISTS "IX_product_treatment_StatusCode"
                ON reporting.report_product_treatments ("StatusCode");

            CREATE TABLE IF NOT EXISTS reporting.report_product_treatment_history (
                "Id" uuid PRIMARY KEY,
                "ReportProductId" uuid NOT NULL,
                "PreviousStatusCode" varchar(80) NOT NULL DEFAULT '',
                "StatusCode" varchar(80) NOT NULL,
                "Note" text NOT NULL DEFAULT '',
                "UpdatedByUserId" uuid NOT NULL,
                "CreatedAt" timestamptz NOT NULL,
                "UpdatedAt" timestamptz NOT NULL,
                CONSTRAINT "FK_product_treatment_history_product" FOREIGN KEY ("ReportProductId")
                    REFERENCES reporting.manual_report_products("Id") ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS "IX_product_treatment_history_Product_CreatedAt"
                ON reporting.report_product_treatment_history ("ReportProductId", "CreatedAt");
            """);
    }
}

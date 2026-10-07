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
            CREATE INDEX IF NOT EXISTS "IX_transfer_feedback_ReportId_FeedbackId_TransferIdentifier_ReceivedAt"
                ON reporting.employer_interface_transfer_feedback ("ReportId", "FeedbackId", "TransferIdentifier", "ReceivedAt");
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
            CREATE INDEX IF NOT EXISTS "IX_contribution_feedback_Product_ReceivedAt"
                ON reporting.employer_interface_contribution_feedback ("ReportProductId", "ReceivedAt");
            CREATE INDEX IF NOT EXISTS "IX_contribution_feedback_ReportId_FeedbackId_ContributionId_ReceivedAt"
                ON reporting.employer_interface_contribution_feedback ("ReportId", "FeedbackId", "ContributionId", "ReceivedAt");
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
            CREATE TABLE IF NOT EXISTS reporting.feedback_problem_resolutions (\n                "Id" uuid PRIMARY KEY,\n                "ProblemId" varchar(180) NOT NULL,\n                "FeedbackId" uuid NOT NULL,\n                "ReportId" uuid NOT NULL,\n                "ReportProductId" uuid NULL,\n                "ContributionId" uuid NULL,\n                "ErrorCode" integer NOT NULL,\n                "ResolutionSource" varchar(80) NOT NULL,\n                "ResolvedByUserId" uuid NOT NULL,\n                "ResolvedAt" timestamptz NOT NULL,\n                "CreatedAt" timestamptz NOT NULL,\n                "UpdatedAt" timestamptz NOT NULL,\n                CONSTRAINT "FK_feedback_problem_resolution_feedback" FOREIGN KEY ("FeedbackId") REFERENCES reporting.employer_interface_feedback("Id") ON DELETE CASCADE,\n                CONSTRAINT "FK_feedback_problem_resolution_report" FOREIGN KEY ("ReportId") REFERENCES reporting.manual_reports("Id") ON DELETE RESTRICT\n            );\n            CREATE UNIQUE INDEX IF NOT EXISTS "UX_feedback_problem_resolution_ProblemId" ON reporting.feedback_problem_resolutions ("ProblemId");\n            CREATE INDEX IF NOT EXISTS "IX_feedback_problem_resolution_Report_Error" ON reporting.feedback_problem_resolutions ("ReportId", "ErrorCode");\n\n            CREATE TABLE IF NOT EXISTS reporting.feedback_resolution_documents (\n                "Id" uuid PRIMARY KEY,\n                "ProblemId" varchar(180) NOT NULL,\n                "ReportId" uuid NOT NULL,\n                "ReportProductId" uuid NULL,\n                "OriginalFileName" varchar(260) NOT NULL,\n                "ContentType" varchar(100) NOT NULL,\n                "Content" bytea NOT NULL,\n                "SizeBytes" bigint NOT NULL,\n                "Sha256" varchar(64) NOT NULL,\n                "UploadedByUserId" uuid NOT NULL,\n                "CreatedAt" timestamptz NOT NULL,\n                "UpdatedAt" timestamptz NOT NULL,\n                CONSTRAINT "FK_feedback_resolution_document_report" FOREIGN KEY ("ReportId") REFERENCES reporting.manual_reports("Id") ON DELETE RESTRICT\n            );\n            CREATE INDEX IF NOT EXISTS "IX_feedback_resolution_document_Problem_CreatedAt" ON reporting.feedback_resolution_documents ("ProblemId", "CreatedAt");\n            """);
    }
}

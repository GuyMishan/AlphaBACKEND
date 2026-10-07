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
            CREATE TABLE IF NOT EXISTS reporting.feedback_problem_resolutions (
                "Id" uuid PRIMARY KEY,
                "ProblemId" varchar(180) NOT NULL,
                "FeedbackId" uuid NOT NULL,
                "ReportId" uuid NOT NULL,
                "ReportProductId" uuid NULL,
                "ContributionId" uuid NULL,
                "ErrorCode" integer NOT NULL,
                "ResolutionSource" varchar(80) NOT NULL,
                "ResolvedByUserId" uuid NOT NULL,
                "ResolvedAt" timestamptz NOT NULL,
                "CreatedAt" timestamptz NOT NULL,
                "UpdatedAt" timestamptz NOT NULL,
                CONSTRAINT "FK_feedback_problem_resolution_feedback" FOREIGN KEY ("FeedbackId") REFERENCES reporting.employer_interface_feedback("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_feedback_problem_resolution_report" FOREIGN KEY ("ReportId") REFERENCES reporting.manual_reports("Id") ON DELETE RESTRICT
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_feedback_problem_resolution_ProblemId" ON reporting.feedback_problem_resolutions ("ProblemId");
            CREATE INDEX IF NOT EXISTS "IX_feedback_problem_resolution_Report_Error" ON reporting.feedback_problem_resolutions ("ReportId", "ErrorCode");

            CREATE TABLE IF NOT EXISTS reporting.feedback_resolution_documents (
                "Id" uuid PRIMARY KEY,
                "ProblemId" varchar(180) NOT NULL,
                "ReportId" uuid NOT NULL,
                "ReportProductId" uuid NULL,
                "OriginalFileName" varchar(260) NOT NULL,
                "ContentType" varchar(100) NOT NULL,
                "Content" bytea NOT NULL,
                "SizeBytes" bigint NOT NULL,
                "Sha256" varchar(64) NOT NULL,
                "UploadedByUserId" uuid NOT NULL,
                "CreatedAt" timestamptz NOT NULL,
                "UpdatedAt" timestamptz NOT NULL,
                CONSTRAINT "FK_feedback_resolution_document_report" FOREIGN KEY ("ReportId") REFERENCES reporting.manual_reports("Id") ON DELETE RESTRICT
            );
            CREATE INDEX IF NOT EXISTS "IX_feedback_resolution_document_Problem_CreatedAt" ON reporting.feedback_resolution_documents ("ProblemId", "CreatedAt");
            CREATE TABLE IF NOT EXISTS reporting.feedback_problem_decisions (
                "Id" uuid PRIMARY KEY,
                "ProblemId" varchar(180) NOT NULL,
                "FeedbackId" uuid NOT NULL,
                "ReportId" uuid NOT NULL,
                "ReportProductId" uuid NULL,
                "ContributionId" uuid NULL,
                "ErrorCode" integer NOT NULL,
                "Outcome" varchar(40) NOT NULL,
                "Note" text NOT NULL DEFAULT '',
                "DecidedByUserId" uuid NOT NULL,
                "DecidedAt" timestamptz NOT NULL,
                "CreatedAt" timestamptz NOT NULL,
                "UpdatedAt" timestamptz NOT NULL,
                CONSTRAINT "FK_feedback_problem_decision_feedback" FOREIGN KEY ("FeedbackId")
                    REFERENCES reporting.employer_interface_feedback("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_feedback_problem_decision_report" FOREIGN KEY ("ReportId")
                    REFERENCES reporting.manual_reports("Id") ON DELETE RESTRICT
            );
            CREATE INDEX IF NOT EXISTS "IX_feedback_problem_decision_Problem_DecidedAt"
                ON reporting.feedback_problem_decisions ("ProblemId", "DecidedAt");
            CREATE INDEX IF NOT EXISTS "IX_feedback_problem_decision_Report_DecidedAt"
                ON reporting.feedback_problem_decisions ("ReportId", "DecidedAt");


            CREATE TABLE IF NOT EXISTS reporting.feedback_external_cases (
                "Id" uuid PRIMARY KEY,
                "OrganizationId" uuid NOT NULL,
                "EmployerId" uuid NOT NULL,
                "CaseKey" varchar(300) NOT NULL,
                "Status" varchar(20) NOT NULL,
                "Destination" varchar(120) NOT NULL,
                "Subject" varchar(300) NOT NULL,
                "MessageTemplate" text NOT NULL DEFAULT '',
                "AssignedToUserId" uuid NULL,
                "CreatedByUserId" uuid NOT NULL,
                "ClosedAt" timestamptz NULL,
                "CreatedAt" timestamptz NOT NULL,
                "UpdatedAt" timestamptz NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_feedback_external_case_Employer_CaseKey"
                ON reporting.feedback_external_cases ("EmployerId", "CaseKey");
            CREATE INDEX IF NOT EXISTS "IX_feedback_external_case_Employer_Status_UpdatedAt"
                ON reporting.feedback_external_cases ("EmployerId", "Status", "UpdatedAt");

            CREATE TABLE IF NOT EXISTS reporting.feedback_external_case_problems (
                "Id" uuid PRIMARY KEY,
                "CaseId" uuid NOT NULL REFERENCES reporting.feedback_external_cases("Id") ON DELETE CASCADE,
                "ProblemId" varchar(180) NOT NULL,
                "FeedbackId" uuid NOT NULL REFERENCES reporting.employer_interface_feedback("Id") ON DELETE RESTRICT,
                "ReportId" uuid NOT NULL REFERENCES reporting.manual_reports("Id") ON DELETE RESTRICT,
                "ReportProductId" uuid NULL,
                "ContributionId" uuid NULL,
                "ErrorCode" integer NOT NULL,
                "CreatedAt" timestamptz NOT NULL,
                "UpdatedAt" timestamptz NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_feedback_external_case_problem_Case_Problem"
                ON reporting.feedback_external_case_problems ("CaseId", "ProblemId");
            CREATE INDEX IF NOT EXISTS "IX_feedback_external_case_problem_Problem"
                ON reporting.feedback_external_case_problems ("ProblemId");

            CREATE TABLE IF NOT EXISTS reporting.feedback_external_case_events (
                "Id" uuid PRIMARY KEY,
                "CaseId" uuid NOT NULL REFERENCES reporting.feedback_external_cases("Id") ON DELETE CASCADE,
                "EventType" varchar(60) NOT NULL,
                "Note" text NOT NULL DEFAULT '',
                "ActorUserId" uuid NOT NULL,
                "CreatedAt" timestamptz NOT NULL,
                "UpdatedAt" timestamptz NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_feedback_external_case_event_Case_CreatedAt"
                ON reporting.feedback_external_case_events ("CaseId", "CreatedAt");

            CREATE TABLE IF NOT EXISTS reporting.feedback_external_case_attachments (
                "Id" uuid PRIMARY KEY,
                "CaseId" uuid NOT NULL REFERENCES reporting.feedback_external_cases("Id") ON DELETE CASCADE,
                "OriginalFileName" varchar(260) NOT NULL,
                "ContentType" varchar(100) NOT NULL,
                "Content" bytea NOT NULL,
                "SizeBytes" bigint NOT NULL,
                "Sha256" varchar(64) NOT NULL,
                "UploadedByUserId" uuid NOT NULL,
                "CreatedAt" timestamptz NOT NULL,
                "UpdatedAt" timestamptz NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_feedback_external_case_attachment_Case_CreatedAt"
                ON reporting.feedback_external_case_attachments ("CaseId", "CreatedAt");


            CREATE TABLE IF NOT EXISTS reporting.feedback_correction_resolution_links (
                "Id" uuid PRIMARY KEY,
                "ProblemId" varchar(180) NOT NULL,
                "SourceReportId" uuid NOT NULL REFERENCES reporting.manual_reports("Id") ON DELETE RESTRICT,
                "WorkspaceReportId" uuid NOT NULL REFERENCES reporting.manual_reports("Id") ON DELETE CASCADE,
                "SourceReportProductId" uuid NULL,
                "ResolverType" varchar(60) NOT NULL,
                "CreatedByUserId" uuid NOT NULL,
                "CreatedAt" timestamptz NOT NULL,
                "UpdatedAt" timestamptz NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_feedback_correction_resolution_link_Problem"
                ON reporting.feedback_correction_resolution_links ("ProblemId");
            CREATE INDEX IF NOT EXISTS "IX_feedback_correction_resolution_link_Workspace_CreatedAt"
                ON reporting.feedback_correction_resolution_links ("WorkspaceReportId", "CreatedAt");
            """);
    }
}

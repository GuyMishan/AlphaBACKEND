-- ALPHA: explicitly authorized DEMO-ONLY report cleanup.
-- Run manually in Supabase SQL Editor after backing up and inspecting the dry run.
-- Replace employer_id and target_ids below; do NOT use for real reporting.
-- SimulationTestMode=ON alone does not prove a report is demo-only.
-- No external vault files, storage objects or audit events are removed.
-- DRY RUN: leave final ROLLBACK. For verified cleanup change ONLY final ROLLBACK to COMMIT.
BEGIN;
CREATE TEMP TABLE demo_cleanup_target_ids (id uuid PRIMARY KEY) ON COMMIT DROP;
INSERT INTO demo_cleanup_target_ids (id) VALUES
 ('2d1e7ee9-22d1-4cac-939a-db8b5794c5db'),
 ('a5ecd452-d513-4082-8af5-826d46c20215'),
 ('5de9a4b2-06f3-4a42-8437-bf11b4102bc8');
CREATE TEMP TABLE demo_cleanup_context (employer_id uuid PRIMARY KEY) ON COMMIT DROP;
INSERT INTO demo_cleanup_context VALUES ('cc4d4f7e-354b-4e24-b2b2-c4efb1fe4cf5');

DO $$
DECLARE n integer;
BEGIN
 SELECT count(*) INTO n FROM reporting.manual_reports r
 JOIN demo_cleanup_target_ids ids ON ids.id=r."Id"
 JOIN demo_cleanup_context ctx ON ctx.employer_id=r."EmployerId";
 IF n <> (SELECT count(*) FROM demo_cleanup_target_ids) THEN
  RAISE EXCEPTION 'Target IDs are missing or belong to another employer';
 END IF;
 IF EXISTS (SELECT 1 FROM reporting.report_transmissions t JOIN demo_cleanup_target_ids ids ON ids.id=t."ReportId"
   WHERE t."Provider" IS NULL OR t."Provider" NOT LIKE 'SimulatedVault%') THEN
  RAISE EXCEPTION 'Non-simulated transmission found';
 END IF;
 IF EXISTS (SELECT 1 FROM reporting.manual_reports r WHERE r."Id" NOT IN (SELECT id FROM demo_cleanup_target_ids)
  AND (r."SourceReportId" IN (SELECT id FROM demo_cleanup_target_ids)
   OR r."RevisionRootReportId" IN (SELECT id FROM demo_cleanup_target_ids)
   OR r."CorrectionWorkspaceId" IN (SELECT id FROM demo_cleanup_target_ids))) THEN
  RAISE EXCEPTION 'Outside report depends on target';
 END IF;
 IF EXISTS (SELECT 1 FROM reporting.feedback_correction_resolution_links l
  WHERE (l."SourceReportId" IN (SELECT id FROM demo_cleanup_target_ids)
   OR l."WorkspaceReportId" IN (SELECT id FROM demo_cleanup_target_ids))
   AND NOT (l."SourceReportId" IN (SELECT id FROM demo_cleanup_target_ids)
   AND l."WorkspaceReportId" IN (SELECT id FROM demo_cleanup_target_ids))) THEN
  RAISE EXCEPTION 'Outside correction link depends on target';
 END IF;
 IF EXISTS (SELECT 1 FROM reporting.manual_report_products p
  JOIN reporting.manual_report_employees e ON e."Id"=p."ReportEmployeeId"
  WHERE e."ReportId" NOT IN (SELECT id FROM demo_cleanup_target_ids)
   AND p."SourceReportProductId" IN (
    SELECT tp."Id" FROM reporting.manual_report_products tp
    JOIN reporting.manual_report_employees te ON te."Id"=tp."ReportEmployeeId"
    WHERE te."ReportId" IN (SELECT id FROM demo_cleanup_target_ids))) THEN
  RAISE EXCEPTION 'Outside product references target';
 END IF;
 IF EXISTS (SELECT 1 FROM reporting.payment_confirmations p WHERE p."ReportId" IN (SELECT id FROM demo_cleanup_target_ids))
 OR EXISTS (SELECT 1 FROM reporting.feedback_external_case_problems p WHERE p."ReportId" IN (SELECT id FROM demo_cleanup_target_ids))
 OR EXISTS (SELECT 1 FROM reporting.feedback_problem_decisions p WHERE p."ReportId" IN (SELECT id FROM demo_cleanup_target_ids))
 OR EXISTS (SELECT 1 FROM reporting.feedback_problem_resolutions p WHERE p."ReportId" IN (SELECT id FROM demo_cleanup_target_ids))
 OR EXISTS (SELECT 1 FROM reporting.feedback_resolution_documents p WHERE p."ReportId" IN (SELECT id FROM demo_cleanup_target_ids)) THEN
  RAISE EXCEPTION 'External evidence or complex feedback needs manual review';
 END IF;
 RAISE NOTICE 'Preflight succeeded for % target reports',n;
END $$;

-- Explicitly inspect which records are in scope, before choosing COMMIT.
SELECT r."Id",r."EmployerId",r."ReportingMonth",r."ReportKind",r."Status"
FROM reporting.manual_reports r JOIN demo_cleanup_target_ids ids ON ids.id=r."Id";
SELECT t."Provider",count(*) FROM reporting.report_transmissions t
JOIN demo_cleanup_target_ids ids ON ids.id=t."ReportId" GROUP BY t."Provider";
SELECT count(*) AS correction_links FROM reporting.feedback_correction_resolution_links l
WHERE l."SourceReportId" IN (SELECT id FROM demo_cleanup_target_ids)
AND l."WorkspaceReportId" IN (SELECT id FROM demo_cleanup_target_ids);

DELETE FROM reporting.feedback_correction_resolution_links
WHERE "SourceReportId" IN (SELECT id FROM demo_cleanup_target_ids)
AND "WorkspaceReportId" IN (SELECT id FROM demo_cleanup_target_ids);
DELETE FROM reporting.employer_interface_contribution_feedback
WHERE "ReportId" IN (SELECT id FROM demo_cleanup_target_ids);
DELETE FROM reporting.employer_interface_transfer_feedback
WHERE "ReportId" IN (SELECT id FROM demo_cleanup_target_ids);
DELETE FROM reporting.employer_interface_feedback
WHERE "ReportId" IN (SELECT id FROM demo_cleanup_target_ids);
UPDATE reporting.manual_reports SET "SourceReportId"=NULL,
 "RevisionRootReportId"=NULL, "CorrectionWorkspaceId"=NULL
WHERE "Id" IN (SELECT id FROM demo_cleanup_target_ids);
DELETE FROM reporting.manual_reports
WHERE "Id" IN (SELECT id FROM demo_cleanup_target_ids)
AND "EmployerId" IN (SELECT employer_id FROM demo_cleanup_context);
DO $$
BEGIN
 IF EXISTS (SELECT 1 FROM reporting.manual_reports r JOIN demo_cleanup_target_ids ids ON ids.id=r."Id") THEN
  RAISE EXCEPTION 'Reports remain after cleanup';
 END IF;
 RAISE NOTICE 'Target reports absent inside transaction';
END $$;
-- DRY RUN DEFAULT. Only change to COMMIT after full verification / backup.
ROLLBACK;

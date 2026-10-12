-- Alpha production read-only data checks (A12.3).
-- Run with psql only after the procedure in docs/release.md#production-read-only-verification is approved:
--   psql "$ALPHA_PROD_READONLY" -v ON_ERROR_STOP=1 -X -f tools/production-readonly/data-checks.sql
-- Returns marker keys, timestamps and counts only; never a stored value. Writes nothing: the session and the
-- transaction are read-only and the script ends with ROLLBACK.
-- The plaintext counts mirror SensitiveDataBackfill.RemainingPlaintextAsync; ProductionReadOnlyChecksTests keeps
-- them in sync and runs this file against a local database.

SET default_transaction_read_only = on;
SET statement_timeout = '60s';
SET lock_timeout = '2s';
BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
SHOW transaction_read_only;

-- 1. One-time job markers.
SELECT "Key", "AppliedAt" FROM identity.data_fixes ORDER BY "AppliedAt";

-- 2. Values not yet in the protected format, per backfill scope.
-- BEGIN plaintext-counts
SELECT scope, remaining FROM (
  SELECT 1 AS ord, 'employees.people' AS scope, count(*) AS remaining FROM employees.people
   WHERE COALESCE("NationalIdEncrypted", '') NOT LIKE 'alpha:v1:%'
  UNION ALL
  SELECT 2, 'employers.employer_payment_accounts', count(*) FROM employers.employer_payment_accounts
   WHERE COALESCE("AccountNumberEncrypted", '') NOT LIKE 'alpha:v1:%' OR COALESCE("AccountHolderIdEncrypted", '') NOT LIKE 'alpha:v1:%'
  UNION ALL
  SELECT 3, 'reporting.manual_reports', count(*) FROM reporting.manual_reports
   WHERE (COALESCE("EmployerRegistrationNumberSnapshot", '') <> '' AND "EmployerRegistrationNumberSnapshot" NOT LIKE 'alpha:v1:%')
      OR (COALESCE("EmployerWithholdingFileNumberSnapshot", '') <> '' AND "EmployerWithholdingFileNumberSnapshot" NOT LIKE 'alpha:v1:%')
      OR (COALESCE("EmployerContactPhoneSnapshot", '') <> '' AND "EmployerContactPhoneSnapshot" NOT LIKE 'alpha:v1:%')
      OR (COALESCE("EmployerContactEmailSnapshot", '') <> '' AND "EmployerContactEmailSnapshot" NOT LIKE 'alpha:v1:%')
      OR (COALESCE("EmployerContactMobileSnapshot", '') <> '' AND "EmployerContactMobileSnapshot" NOT LIKE 'alpha:v1:%')
  UNION ALL
  SELECT 4, 'reporting.manual_report_employees', count(*) FROM reporting.manual_report_employees
   WHERE "NationalId" NOT LIKE 'alpha:v1:%' OR "InterfaceIdentifier" NOT LIKE 'alpha:v1:%'
      OR (COALESCE("EmailSnapshot", '') <> '' AND "EmailSnapshot" NOT LIKE 'alpha:v1:%')
      OR (COALESCE("MobileSnapshot", '') <> '' AND "MobileSnapshot" NOT LIKE 'alpha:v1:%')
  UNION ALL
  SELECT 5, 'reporting.manual_report_payments', count(*) FROM reporting.manual_report_payments
   WHERE COALESCE("EmployerAccount", '') <> '' AND "EmployerAccount" NOT LIKE 'alpha:v1:%'
  UNION ALL
  SELECT 6, 'reporting.report_transmissions', count(*) FROM reporting.report_transmissions
   WHERE (length("Payload") > 0 AND substring("Payload" from 1 for 4) <> '\x414c5001'::bytea)
      OR (COALESCE("ResponsePayload", '') <> '' AND "ResponsePayload" NOT LIKE 'alpha:v1:%')
  UNION ALL
  SELECT 7, 'reporting.manual_report_attachments', count(*) FROM reporting.manual_report_attachments
   WHERE length("Content") > 0 AND substring("Content" from 1 for 4) <> '\x414c5001'::bytea
  UNION ALL
  SELECT 8, 'reporting.employer_interface_feedback', count(*) FROM reporting.employer_interface_feedback
   WHERE COALESCE("RawXml", '') <> '' AND "RawXml" NOT LIKE 'alpha:v1:%'
) counts ORDER BY ord;
-- END plaintext-counts

ROLLBACK;

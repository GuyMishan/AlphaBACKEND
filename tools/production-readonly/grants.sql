-- Least privilege for tools/production-readonly/data-checks.sql: only the columns its counts filter on.
-- The database owner runs this after creating the login (see docs/release.md#production-read-only-verification):
--   CREATE ROLE alpha_readonly_checks LOGIN PASSWORD '<generated>' CONNECTION LIMIT 1 VALID UNTIL '<tomorrow>';
-- ProductionReadOnlyChecksTests applies this file locally and proves the checks run with it and nothing more.

ALTER ROLE alpha_readonly_checks SET default_transaction_read_only = on;
GRANT USAGE ON SCHEMA identity, employees, employers, reporting TO alpha_readonly_checks;
GRANT SELECT ("Key", "AppliedAt") ON identity.data_fixes TO alpha_readonly_checks;
GRANT SELECT ("NationalIdEncrypted") ON employees.people TO alpha_readonly_checks;
GRANT SELECT ("AccountNumberEncrypted", "AccountHolderIdEncrypted") ON employers.employer_payment_accounts TO alpha_readonly_checks;
GRANT SELECT ("EmployerRegistrationNumberSnapshot", "EmployerWithholdingFileNumberSnapshot", "EmployerContactPhoneSnapshot",
              "EmployerContactEmailSnapshot", "EmployerContactMobileSnapshot") ON reporting.manual_reports TO alpha_readonly_checks;
GRANT SELECT ("NationalId", "InterfaceIdentifier", "EmailSnapshot", "MobileSnapshot") ON reporting.manual_report_employees TO alpha_readonly_checks;
GRANT SELECT ("EmployerAccount") ON reporting.manual_report_payments TO alpha_readonly_checks;
GRANT SELECT ("Payload", "ResponsePayload") ON reporting.report_transmissions TO alpha_readonly_checks;
GRANT SELECT ("Content") ON reporting.manual_report_attachments TO alpha_readonly_checks;
GRANT SELECT ("RawXml") ON reporting.employer_interface_feedback TO alpha_readonly_checks;

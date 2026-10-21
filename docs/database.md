# Alpha database

One PostgreSQL database, one `AlphaDbContext`, eight schemas, 68 tables. This document covers ownership, how the schema is created and changed, and the rules for changing it safely while production is live.

## Staged ALPHA payment setup (October 2026)

The additive `billing.pending_payment_setup_details` table stores the proposed invoicing identity/address as JSON until the payment provider confirms a token via a verified callback. It is keyed by the billing account and correlated to `PendingSetupReference`. The account's published details are unchanged while payment setup is pending; on verified callback, the details and payment-token metadata are written in a single transaction and the staged draft is deleted. A failed or cancelled hosted checkout must not activate or overwrite the previous account. An unconfirmed attempt expires after 30 minutes; later callbacks are ignored, and a new attempt can replace the expired draft. The completed reference is retained in the staging table for checkout-specific status verification. The provider never receives a PAN or CVV from ALPHA, and these are not stored in staging. This table is intentionally raw SQL, with no new EF DbSet.

## How the schema is built

There are **no EF Core migrations**. The schema is the EF model plus 32 idempotent compatibility initializers (`src/Alpha.Infrastructure/Persistence/*SchemaInitializer.cs`, `*Initializer.cs`), executed on every startup by `AlphaDatabaseInitializer` in a fixed order:

1. If `Database:BootstrapEmptyDatabase` is true (default: Development only), `EnsureCreatedAsync()` creates the EF model. It is a no-op when the database already has any table.
2. The 32 initializers run in the production order (`ALTER ... ADD COLUMN IF NOT EXISTS`, `CREATE ... IF NOT EXISTS`, `CREATE OR REPLACE FUNCTION`, idempotent seed/upsert of codebooks).
3. Reporting runs `LegacyFeedbackNormalization`: a one-time normalization of feedback stored before ingestion normalized it. It holds one payload at a time, never fails startup because of one row, and records `legacy-feedback-normalization-v1` after a clean pass; later starts skip it.

Production never runs `EnsureCreated` unless the flag is set explicitly; a production database therefore only receives the idempotent initializer DDL on deploy.

The order is pinned by `CompatibilityBaselineTests.Schema_initializers_run_from_one_owner_in_the_production_order`, the initializer SQL by `Schema_initializer_sql_changes_are_reviewed`, the table set by `Database_table_definitions_match_the_baseline` and the EF model by `EfModelBaselineTests` (SHA-256 of the generated DDL).

### Blank-database bootstrap (fixed in A12)

Before A12 a blank database could not start: `PensionFundSnapshotSchemaInitializer` backfilled from `reference_data.pension_products`, which `ReferenceDataSchemaInitializer` creates seven steps later, and nothing created the base tables because no migrations exist. Test fixtures worked around it by pre-creating the schema.

Fix, without reordering production: the backfill now runs only when `to_regclass('reference_data.pension_products')` exists (on a blank database there are no rows to backfill), and `AlphaDatabaseInitializer` can bootstrap the EF model first. Reordering was rejected because the column types each initializer creates differ and production was built in the current order.

Evidence: `DatabaseBootstrapIntegrationTests` (blank database succeeds; without bootstrap it fails instead of half-creating; re-running on a populated database preserves row counts and still runs the backfill). The blank-database test fails with `42P01 relation "reference_data.pension_products" does not exist` when the guard is removed.

## Schema verification

### Release candidate versus production code

The A12 tree was ported onto `origin/main` `90c7ba6` (the code production runs; schema-relevant code unchanged since `dc5c609`) and compared on isolated PostgreSQL 17:

| Check | Result |
|---|---|
| EF `GenerateCreateScript()` | identical to `dc5c609` (no Domain/Infrastructure change up to `90c7ba6`) |
| Applied schema (EF model + 31 initializers) | identical: 851 columns, 273 indexes, 151 constraints, 4 triggers, 5 functions, 67 tables, identical seeded rows |
| Route declarations | 245, identical set |
| Initializer order | identical (31) |

**Port of `main` up to `46202eb` (2026-10-12).** `main` commits after `90c7ba6` were merged into the module layout. The only schema change among them is `main`'s own `678240b`: `reporting.employer_interface_report_product_data."CollectiveAgreementRefund" boolean NULL` (`ADD COLUMN IF NOT EXISTS` in `EmployerInterface006SchemaInitializer`, nullable, no default, no backfill, no lock beyond a catalog-only `ALTER`). It is already part of the code `main` deploys, so it is expected to exist in production before this release. The EF `GenerateCreateScript()` of this tree is byte-identical to `46202eb`; `EfModelBaselineTests` and the initializer fingerprint were updated for this column only.

The A12/A12.1 initializer edits do not change the resulting schema: the `to_regclass` guard above, and the removal of a one-time data statement from `IdentitySchemaInitializer` that rewrote every user's e-mail on any database without its 2026-09-21 marker (dangerous on restored backups; already applied in production). **No migration is required.**

### Production schema verification

A fresh database is built from the EF model first, while production was built over time by initializers, earlier model versions and at least one manual Supabase migration. Production can therefore differ physically from a fresh bootstrap. `tools/Alpha.SchemaVerifier` checks that safely:

- It reads **only `pg_catalog`** (tables, columns with type/nullability/default, constraints and non-constraint indexes keyed by definition, triggers, functions, sequences, views, enums, extensions) in the eight application schemas. `pg_catalog` is visible with `CONNECT` alone, so the capture role needs no access to any table data.
- The session runs with `default_transaction_read_only=on` inside an explicit `READ ONLY` transaction and refuses to continue if the server does not confirm read-only mode. Connection strings come from environment variables and are never printed.
- Differences are classified: **Blocking** (a required object is missing, a column type differs, a column is NOT NULL where the application may write NULL, an extra NOT NULL column without default), **Review** (extra constraint, unique index or trigger; missing non-unique index; nullability or default difference), **Info** (production-only objects, names, extensions). Exit code 2/1/0.

The production procedure (logins, exact commands, expected output and stop criteria) is in [release.md](release.md#production-read-only-verification) and runs only after explicit approval; locally, `SchemaVerifierTests` exercise the same `expected`/`capture`/`compare` steps.

Expected production-only results (Info/Review, not blocking): the view `reference_data.pension_products_by_manufacturer` (manual Supabase migration, 2026-10-08), and possibly initializer-era constraints such as a foreign key on `identity.user_sessions`. Any **Blocking** result stops the release. The `*.schema.json` outputs contain only catalog metadata but should still be kept out of the repository (they are git-ignored).

`SchemaVerifierTests` prove determinism, the classification of simulated historical drift (including the `user_sessions` foreign key) and that capture works with a `CONNECT`-only role that cannot read data or write.

## Inventory and ownership

Owner = the module that defines the invariants and is the primary writer (also enforced for `DbSet` access in `ModuleOwnershipTests`). Tenant scope: **O/E** = row carries `OrganizationId`/`EmployerId`; **parent** = reached only through a tenant-scoped parent; **user** = per user; **global** = shared catalog. Sensitive: encrypted (enc), lookup hash (hash) or document/payload content.

| Table | Owner | Purpose | Tenant scope | Sensitive |
|---|---|---|---|---|
| audit.audit_events | Auditing | Append-only audit trail (no FKs by design) | O/E optional | client IP, user agent |
| billing.billing_accounts | Billing | Organization or employer billing account | O/E | — |
| billing.billing_account_pricing_components | Billing | Per-account pricing overrides | parent | — |
| billing.billing_account_pricing_tiers | Billing | Tiered pricing | parent | — |
| billing.billing_periods | Billing | Billed periods (unique per account/period) | parent | — |
| billing.billing_usage | Billing | Usage lines per period | parent, E | — |
| billing.payments | Billing | Charges with idempotency keys | parent | — |
| billing.payment_attempts | Billing | Provider attempts | parent | — |
| billing.payment_methods | Billing | Provider tokens and card metadata | parent | provider token |
| billing.refunds | Billing | Refunds with idempotency keys | parent | — |
| billing.provider_webhook_events | Billing | Webhook dedup and status | global | payload hash |
| employees.people | Workforce | Persons | O | national ID (enc, hash) |
| employees.employments | Workforce | Employment of a person at an employer | O/E | salary |
| employees.employee_pension_products | Workforce | Employee pension mix products | parent | — |
| employees.employee_pension_contributions | Workforce | Mix contribution percentages | parent | — |
| employers.employers | Workforce | Employers | O | — |
| employers.employer_profile_settings | Workforce | Employer settings incl. billing inheritance | E | — |
| employers.employer_user_access | IdentityAccess | Per-employer user grants | O/E | — |
| employers.employer_documents | Reporting | 006 document library (types 3–6), reusable PDF evidence | O/E | content (enc) |
| employers.employer_payment_accounts | Reporting | Pension payment bank accounts | O/E | account number and holder ID (enc, hash) |
| employers.bank_debit_mandates | Reporting | Bank debit mandates | parent | — |
| identity.users | IdentityAccess | Users | user | national ID, phone (enc, hash) |
| identity.user_sessions | IdentityAccess | Server-side sessions | user | — |
| identity.otp_challenges | IdentityAccess | Login OTP (retention 24 h) | user | code hash |
| identity.registration_otp_challenges | IdentityAccess | Registration OTP | user | national ID, phone (enc, hash), token hash |
| identity.user_invitations | IdentityAccess | Invitations (token hash) | O/E | token hash |
| identity.referent_organization_assignments | IdentityAccess | Referent scope | O | — |
| identity.referent_employer_assignments | IdentityAccess | Referent scope | E | — |
| identity.data_fixes | Infrastructure (`DataFixMarkers`) | Completion markers of one-time jobs: `sensitive-data-backfill-v1`, `legacy-feedback-normalization-v1` (and the historical 2026-09-21 e-mail fix) | global | — |
| organizations.organizations | Organizations | Tenants | — | — |
| organizations.organization_profile_settings | Organizations | Organization profile | O | — |
| organizations.organization_memberships | IdentityAccess | Memberships, roles, permissions | O | — |
| reference_data.banks, bank_branches, cities, streets | ReferenceData | Public catalogs (synchronized) | global | — |
| reference_data.pension_products | ReferenceData | Pension product catalog | global | — |
| reference_data.sync_runs | ReferenceData | Synchronization log | global | — |
| reference_data.select_options, salary_layers | ReferenceData | Seeded option lists | global | — |
| reference_data.employer_interface_006_options, employer_interface_006_error_codes, employer_interface_006_fields | Reporting | Seeded 006 codebooks and the V6 field map (from the official workbook) | global | — |
| reporting.contribution_percentage_limits | Reporting | Seeded statutory limits | global | — |
| reporting.manual_reports | Reporting | Reports and lifecycle | O/E | employer identifiers/contacts (enc), masked account |
| reporting.manual_report_employees | Reporting | Employee snapshot per report | O/E | national/interface ID (enc, hash), contacts (enc) |
| reporting.manual_report_products | Reporting | Product lines | parent | — |
| reporting.manual_contributions | Reporting | Contribution lines | parent | — |
| reporting.manual_report_payments | Reporting | Deposit details | parent | employer account (enc) |
| reporting.manual_report_attachments | Reporting | Report documents | parent | content (enc) |
| reporting.payment_confirmations | Reporting | Payment evidence metadata | parent | — |
| reporting.report_transmissions | Reporting | Transmission attempts | O/E | payload and response (enc) |
| reporting.employer_interface_file_sequences | Reporting | 006 file sequence numbers | global | — |
| reporting.employer_interface_feedback | Reporting | Clearinghouse feedback files | O/E | raw XML (enc) |
| reporting.employer_interface_contribution_feedback, employer_interface_transfer_feedback | Reporting | Parsed feedback lines | parent | — |
| reporting.employer_interface_report_product_data | Reporting | 006 product metadata | parent | — |
| reporting.report_product_treatments, report_product_treatment_history | Reporting | Feedback treatment status | parent | — |
| reporting.feedback_problem_resolutions, feedback_problem_decisions, feedback_correction_resolution_links | Reporting | Feedback resolution | parent | — |
| reporting.feedback_resolution_documents | Reporting | Resolution documents | parent | content (enc) |
| reporting.feedback_external_cases | Reporting | External cases (no FK on tenant columns) | O/E | — |
| reporting.feedback_external_case_problems, feedback_external_case_events | Reporting | Case details | parent | — |
| reporting.feedback_external_case_attachments | Reporting | Case attachments | parent | content (enc) |

`reference_data.manufacturer_company_groups` is now provisioned with its minimal shape on new databases by `ReferenceDataSchemaInitializer`, but manufacturer-to-company mappings must still be reviewed and populated explicitly; initializer DDL never fabricates routing mappings.

Retention: `SecurityRetentionHostedService` deletes only OTP challenges older than `Security:OtpRetentionHours` and expired, non-pending invitations older than `Security:ExpiredInvitationRetentionDays`. Business, reporting, financial and audit rows are never deleted by jobs.

## Data-model observations (not changed in A12)

Each item failed the change-necessity gate below for now: none is a proven defect in current code paths, and each needs production data to be checked first.

| Observation | Risk | Recommendation |
|---|---|---|
| Tenant consistency between child and parent rows (for example `employments.OrganizationId` vs `employers.OrganizationId`) is code-enforced only | A coding error could create cross-tenant inconsistency | Count mismatches on a sanitized production copy; if zero, add composite foreign keys `(Id, OrganizationId)` in an expand/validate rollout |
| `reporting.feedback_external_cases` has no FK on `OrganizationId`/`EmployerId` | Orphans after deletions/transfers | Same: measure, then add FK `NOT VALID` → `VALIDATE` |
| No row-level security | Isolation depends on application checks | Evaluate RLS only together with a least-privilege runtime role |
| `audit.audit_events` append-only only in the application | Raw SQL could rewrite audit | Revoke `UPDATE/DELETE` from the runtime role once a separate migration role exists |
| Fresh bootstrap vs historical production schema may differ (see caveat above) | Behavior differences between environments | Schema-dump comparison as a release gate |

## Change policy

A schema change is allowed only when there is a proven business, security, operational or correctness need. For each change record, in the pull request and in this file:

1. The defect or need, and the evidence (code, data, failing test).
2. Why a code-only fix is insufficient, the alternatives, and the simplest option.
3. How existing data and relationships are preserved.
4. Lock, downtime, backfill, uniqueness and large-table risks.
5. The upgrade, compatibility and rollback/roll-forward strategy.
6. The test that proves it on an empty **and** a populated database.

Mechanics:

- Express the change as idempotent DDL in a new or existing initializer (or introduce reviewed EF migrations as a deliberate, documented switch). Never edit a destructive statement into an initializer that runs on every start.
- Prefer expand → backfill → validate → contract across separate releases. Add constraints `NOT VALID` first, validate later. Create large indexes `CONCURRENTLY` outside the startup path.
- Update the EF model, `EfModelBaselineTests`, the initializer fingerprint and, if tables change, the table baseline in the same change.
- Verify on the isolated database ([development.md](development.md#postgresql-test-database)). Never run DDL, migrations or tests against production, staging or a shared database.

## Local databases

- Development: `docker compose up -d postgres` (port 5432, database `alpha`, persistent volume) — `dotnet run` bootstraps it when empty.
- Tests: `docker-compose.test.yml` (loopback port 55432, database `alpha_tests`, tmpfs). `PostgresTestEnvironment` refuses any non-loopback host or other database name; integration tests create and drop their own databases on that server.

## Legal document versions (2026-10-18)

`reference_data.legal_document_versions` is owned by ReferenceData, global (not tenant-scoped). One draft at a time, monotonically numbered versions, immutable published Markdown and audit timestamps. The additive initializer is idempotent and runs after the legacy initializers. No existing data is rewritten. Roll forward by fixing the initializer; do not drop published evidence. The previous 67-table/31-initializer compatibility hashes remain pinned to historical tables and initializers; this new table/initializer has explicit compatibility assertions. Initial rollout has no published legal content; a platform admin must publish an approved version. Test both empty and populated isolated databases before deployment.

## Legal document v1 release (2026-10-18)

The initial approved-for-publication v1 Markdown is held at `docs/legal/ALPHA_LEGAL_POLICY_V1.md` and embedded in Alpha.Infrastructure. On startup `LegalDocumentSchemaInitializer.PublishVersionOneIfEmptyAsync` publishes version 1 only if `reference_data.legal_document_versions` is empty; **it never modifies any existing legal document or draft**. Once populated, all further changes go through the platform admin publishing endpoints. If an existing draft is present in an environment, an administrator must review and publish it; startup must not overwrite it. A smoke test of the anonymous `/api/legal-document/active` endpoint after deployment is required. This release stores an initial draft adapted to ALPHA and needs legal/operator-identity review; the signed-off business owner explicitly requested its publication.

### Container build legal-resource guard

The legal v1 Markdown is an embedded resource in Alpha.Infrastructure. The Docker build context must explicitly include `docs/legal/ALPHA_LEGAL_POLICY_V1.md` even though other `docs/` files are excluded. `.dockerignore` has targeted reinclusion rules; `CompatibilityBaselineTests.Docker_context_includes_embedded_legal_policy` guards the file and project reference. A .NET-only solution build cannot detect Docker context omissions, so also verify the Render Docker deployment succeeds.

## Employer legal agreement document type

The existing `employers.employer_documents` table gains document type 7 through the idempotent `EmployerDocumentSchemaInitializer`, widening the existing CHECK constraint while retaining the same entity/table and encrypted file storage. This is an additive document-type change, not a new table. Existing document records are preserved.

## Consent schema

RegistrationConsentSchemaInitializer adds `identity.registration_consent_intents`, `identity.user_consent_events` (append-only technical evidence), and user columns `AcceptedLegalVersion`, `AcceptedLegalAt`, `MarketingEmailOptIn`. Registration binds acceptance to OTP, keeping marketing separate. No consent management screen is introduced. Schema verifier count becomes 70 tables.

## Selected pension funds (October 2026)

`reference_data.pension_products.is_system_default_fund` marks catalog funds identified from the official 2024–2028 selected-funds list (comprehensive pension funds managed by Meitav, Mor, Altshuler Shaham or Infinity). Employer-specific overrides live in `employers.employer_default_funds` as an explicit set of active pension-fund external keys. Absence of a row means inherited system selection; an empty array is an intentional empty employer selection. DELETE of the employer override restores inheritance. Catalog sync must reapply the official selection classification whenever reference products are replaced. The selected-fund flag is not proof of a particular employee's enrollment or of eligibility under the 2025 ID-digit allocation rule.

## Manufacturer contact email directory (October 2026)

The ReferenceData startup initializer creates `reference_data.manufacturer_contact_emails` (primary key `manufacturer_name`) and `reference_data.product_contact_email_overrides` (primary key `external_key`). Both store contact `email`, `is_verified`, `updated_at` and `updated_by`. They are platform-managed metadata, not tenant-authored pension records. A verified product override takes precedence over the verified parent manufacturer address. Unverified or absent contacts are not eligible for sending. The parent is resolved via the verified `reference_data.manufacturer_company_groups` legal-company mapping, never from fund code alone. Removing an override restores fallback; no email is sent by these endpoints.

## Manufacturer inquiry email templates (October 2026)

`reference_data.manufacturer_error_email_templates` stores a single optional subject/body per official active feedback error code, with an explicit `is_enabled` flag (default false), `updated_by` and timestamp. The platform ReferenceData endpoint restricts mutation to platform admins and validates template placeholders against a small allowlist. No message is sent by the template API; future dispatch must independently enforce authorization, verified recipient resolution and personal-data minimization.

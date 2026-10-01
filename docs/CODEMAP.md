# ALPHA Backend code map

## Solution layers
- `src/Alpha.Api` — HTTP endpoints, auth, middleware, provider adapters and hosted API services.
- `src/Alpha.Application` — authorization, billing, entitlements, identity and reporting orchestration.
- `src/Alpha.Domain` — domain entities and financial/reporting models.
- `src/Alpha.Infrastructure` — EF Core, schema initialization and persistence configuration.
- `src/Alpha.Worker` — background worker boundary.
- `tests/*` — API/domain tests.

## Authentication, users and access
- Internal referents: `Identity/ReferentAssignments.cs`, `ReferentEndpoints.cs`, `ReferentSchemaInitializer.cs`, and `OrganizationAccessService.cs`. Separate cross-organization assignments never count toward tenant billing seats.
- API: `Authentication/*`, `Endpoints/AuthEndpoints.cs`, `AccessEndpoints.cs`, `InvitationEndpoints.cs`, `ScopeEndpoints.cs`.
- Application: `Authorization/OrganizationAccessService.cs`, `Identity/InvitationService.cs`.
- Domain: `Identity/*`, `Organizations/OrganizationMembership.cs`, `Employers/EmployerUserAccess.cs`.
- Persistence: identity/access/invitation schema initializers.

## Organizations, employers and employees
- `src/Alpha.Api/Services/EmployerTransferService.cs` coordinates admin-only ownership transfer (`POST /api/organizations/{organizationId}/employers/{employerId}/transfer`), identity collision and target quota checks, shared Person cloning, report/feedback/account scope updates and employer-specific access re-scoping within one PostgreSQL transaction.
- Organization types 7/8 represent small (one employer) and regular organizations. Small-organization employer creation, inbound transfer and conversion to small are validated by the backend; legacy type values 1–6 remain readable, while self-service onboarding continues to use 6.
- Self-service onboarding creates organizations with the name `ארגון - {employer name}`, keeping the employer legal name unchanged.
- Endpoints: `OrganizationEndpoints.cs`, `EmployerEndpoints.cs`, `DashboardEndpoints.cs`, profile endpoints, employee pension mix endpoints. `EmployerEndpoints.cs` owns employer lifecycle status changes; authorized employer managers may set only Active or Closed (shown in the UI as "פעיל" / "מבוטל") and every change is audited. Legacy Onboarding/Suspended employer rows are normalized to Active at startup, and new employers start Active. `DashboardEndpoints.cs` returns server-side aggregate counts for the authorized platform/organization/employer scope and never materializes employee lists for dashboard statistics.
- Domain: `Organizations/*`, `Employers/*`, `Employees/*`. Employee master identity supports Employer Interface 006 identifier types 1 (Israeli ID) and 2 (passport); identifiers are encrypted at rest and uniqueness/search is scoped by organization + identifier type + lookup hash. Existing employees are backfilled as Israeli-ID identities by `EmployeeIdentitySchemaInitializer`.

## Billing, entitlements and payment providers
- Endpoints: `BillingAccountEndpoints.cs`, `BillingManagementEndpoints.cs`, `PaymentProviderEndpoints.cs`.
- Application: `Billing/*`, `Entitlements/*`.
- Provider boundary: `Alpha.Application/Billing/IPaymentProvider.cs`.
- Adapters: `Alpha.Api/Services/CardComPaymentProvider.cs`, `PayPlusPaymentProvider.cs`, `PaymentProviderResolver.cs`. Provider callbacks persist a payload hash/idempotency record rather than the raw callback body; provider exception details are not returned to API callers.
- Persistence: `Billing*SchemaInitializer.cs`, `BillingConfigurations.cs`.
- Free-plan quota mutations are serialized per organization by `Alpha.Infrastructure/Persistence/OrganizationEntitlementLock.cs`; guarded writes require an explicit transaction commit and otherwise roll back; flows that already own a transaction (such as invitation acceptance during registration) join the same organization advisory lock. PostgreSQL concurrency/rollback coverage lives in `OrganizationEntitlementLockConcurrencyTests.cs`.

## Employer Interface 006 / reporting
- Endpoints: `EmployerInterfaceEndpoints.cs`, `ManualReportEndpoints.cs`, `ReportValidationEndpoints.cs`, `ReportTransmissionEndpoints.cs`, `ReportFeedbackEndpoints.cs`, `DerivedReportEndpoints.cs`.
- Transmission endpoints suppress provider/internal exception details from client-visible responses. Employer Interface 006 transmission evidence, clearinghouse feedback XML, report PDF attachments, employer bank-account snapshots, report employee identifiers/contact details, employer registration/contact snapshots and provider response payloads are encrypted at rest with purpose binding; employee identifier searches use a purpose-bound lookup hash; attachments and report identifiers are decrypted only at authorized API/export boundaries while their evidence hash/size remain bound to the original plaintext. The startup sensitive-data backfill encrypts legacy plaintext evidence idempotently. Derived/correction reports decrypt authorized source snapshot secrets — including employer identity/contact snapshots, employee identifiers/contact snapshots and report-product payment accounts — and re-encrypt them under the new report/record IDs before persistence, preserving purpose-bound encryption. Report creation and transmission use dedicated `CanCreateReport` / `CanTransmitReport` permissions. Existing memberships are migrated from the previous employee-create permission to preserve effective access.
- Core services: `EmployerInterfaceService.cs`, `EmployerInterface006ExportService.cs`, `EmployerInterface006XmlBuilder.cs`, `EmployerInterface006WorkbookRules.cs`, `EmployerInterfaceSchemaRegistry.cs`, file naming/sequence services. XML/DAT/TST report imports are bound to the selected employer registration number and exactly one salary month before persistence, preserve employee identifier type (Israeli ID/passport) into the employee master and report snapshot, and imported employer payment-account snapshots follow the same encrypted-at-rest contract as manual/Excel reporting. Official summary feedback remains encrypted immutable evidence in `EmployerInterfaceFeedback` and is additionally normalized at ingestion into transfer-level money status and contribution-level manufacturer/right-registration rows for queryable reconciliation. Manual product treatment status/note is stored separately with append-only history; neither source overwrites the employer report snapshot.
- Reporting application/domain: `Alpha.Application/Reporting/*`, `Alpha.Domain/Reporting/*`.
- Persistence: `EmployerInterface006*Initializer.cs`, reporting/report lifecycle/transmission/feedback configurations and initializers.
- Tests: `EmployerInterface006*Tests.cs`, `ReportingSchemaSqlGuardTests.cs`.
- Formal correction creation fails closed when the selected immutable source product is missing Employer Interface metadata; negative corrections must originate from a transmitted/completed (or officially external) current report, and current operation 2/3 corrections must originate from negative operation 6 data. Reports & Feedback receives correction eligibility from the backend rather than guessing from status/kind in the UI.

## Authoritative 006 specifications
- `docs/specifications/employer-interface/006/Employer interface V 6.xlsx`.
- XSDs in the same directory.
- Clearing-house rules PDF and error-code workbook under `docs/specifications/mislaka`.
These sources outrank assumptions, old examples and UI behavior.

- V006 export groups by fund; official workbook validation rejects inconsistent per-product transfer/payment details within a fund rather than silently selecting a conflicting payment.
- Deposit list account resolution: `ManualReportEndpoints.GetDepositsAsync` resolves the current reference fund bank details when a draft has no persisted payment entry, so the list and payment editor agree. The endpoint also accepts an optional exact `reportProductId` filter so Reports & Feedback opens the canonical payment editor for the selected employee+product without relying on text search.
- Malware scanning: `ConfiguredMalwareScanner` implements Cloudmersive's documented multipart/JSON protocol (and retains the generic adapter); fail-closed scanning applies to both report PDFs and payment evidence. Free evaluation uploads are capped to 3 MB by the shared scanner configuration and endpoints.
- Payment evidence: `PaymentConfirmationEndpoints.cs` validates tenant/report-product access and uses `PaymentEvidenceStorage` (private Supabase Storage bucket) for screened PDF/JPEG/PNG confirmations. PostgreSQL `reporting.payment_confirmations` holds immutable versioned metadata; it is not an official Employer Interface attachment.

## Security
- `src/Alpha.Api/Security/*` — versioned AES-GCM string/binary encryption and legacy-compatible sensitive-data backfill, headers, audit, malware scanning, retention and session activity. `SessionActivityMiddleware` rejects revoked/expired/idle sessions and sessions whose user has been deactivated. `/api/auth/logout` revokes the current server-side session, so the associated JWT cannot continue through the middleware. Login OTP requests use a neutral unknown-account response, and OTP HMAC material is domain-separated from JWT signing.
- `src/Alpha.Domain/Auditing/AuditEvent.cs`.

## Database
- `AlphaDbContext.cs` / `AlphaDbContextFactory.cs`.
- `EntityConfigurations.cs`, `ReportingEntityConfigurations.cs` and focused schema initializers.
- Check existing initializer/configuration patterns before introducing a new table or column.

## CI
- `.github/workflows/ci.yml` restores, Release-builds and tests the full solution.

- Access management candidate discovery is organization-scoped; removal or demotion of the last active organization admin is blocked, and access role/mode enum inputs are validated before persistence.
- Login OTP requests return an opaque synthetic challenge identifier for unknown identities so response shape does not disclose account existence. Registration requests are likewise neutral for existing identities, and registration advisory locks use purpose-bound lookup material rather than plaintext identity values.

- The existing `GET /manual-reports` endpoint returns paged, scope-authorized **editable** drafts only (Draft, ReadyForValidation, Error), including report kind, source and payment-account identity. `GET /manual-reports/{id}` includes the same resume metadata. The frontend restores persisted drafts without creating a duplicate.

- The startup reference-data initializer now persists the official Hebrew labels for all V006 `receipt-type` codes (1/2/4/6/8) rather than opaque `קוד N` placeholders; frontend table and selectors resolve descriptions via the existing `/api/reference-data/employer-interface-006/options` endpoint.

- Payment confirmations also expose an employer-scope-authorized report-level GET listing for the deposit table, avoiding one evidence request per product; actual downloads remain per-product and SHA256 checked.

- Empty manual/editor contribution placeholders (amount, rate, exemptions all zero) are skipped when saved; legacy empty placeholders are also omitted from current-report V006 serialization and business checks. Real contributions with a missing mandatory rate or forbidden component remain invalid; negative report semantics remain unchanged. Regression coverage: `EmployerInterface006XmlBuilderTests`.


- Editing a person's postal code/post-office box in their employee card updates matching address snapshots in still-editable reports for the same authorized employment, in the same transaction. Imported/overridden snapshots and immutable submitted/sent reports remain untouched; refreshed drafts are marked dirty and revalidated. Address fields have no duplicate editor inside the report wizard.

- `EmployerInterfaceLineFeedbackParser` extracts per-record statuses from encrypted, XSD-validated official summary feedback. The scoped feedback-details endpoint associates them only via saved exported contribution record identifiers, preserving official file provenance and never guessing associations for unmatched feedback.


- Report-feedback normalization is idempotently backfilled at API startup for already-correlated encrypted summary feedback, so historical reports receive the same money/contribution projections as newly ingested feedback.
- Derived/correction drafts accept an optional source-product selection; selected products and their owning employees are cloned with the same immutable snapshot/previous-record semantics, enabling a focused employee+product correction workflow without mutating the submitted source report.

- Reports & Feedback exports are generated server-side from authorized report snapshots/normalized feedback as UTF-8 BOM CSV: employee contribution detail, deposit summary and manufacturer feedback. Export reads never mutate submitted evidence.


- Feedback ingestion is idempotent under concurrent duplicate uploads: the unique employer+payload-hash index is the database boundary and a losing concurrent request resolves to the already-persisted feedback instead of surfacing a 500.
- Report transmission atomically claims Validated -> Processing before file reservation. After the transmission evidence row is persisted, provider dispatch and result persistence are intentionally detached from the HTTP request-abort token so a browser disconnect cannot cancel an irreversible provider side effect and leave a misleading retryable local state.

- The normalized contribution-feedback table has a dedicated `(ReportProductId, ReceivedAt)` index for the Reports & Feedback per-product modal; the broader report/product index remains for report-level reconciliation.

- Treatment updates use client-observed optimistic concurrency on `UpdatedAt`: the modal sends the version it loaded, stale operators receive a conflict even when their save starts after another operator already committed, and EF's concurrency token still protects overlapping writes. The successful update, history row and audit event remain atomic in one SaveChanges transaction. Report/feedback list indexes cover scope+updated ordering and active feedback report/transmission/contribution lookups so the operational screen does not depend on avoidable full-table scans as data volume grows.

- Clearing-house transmission fails closed when no real provider is configured. `MockReportTransmissionProvider` is opt-in (development or explicit `EmployerInterface006:AllowMockTransmission`) and is not the production default. Production V006 generation also rejects the built-in `000000000` recipient placeholder.


## Correction workspaces, retransmission and immutable history
- Sent reports are immutable. Correcting a sent current report creates or reuses one internal full-report correction workspace linked by `ManualReport.SourceReportId`; the original report remains unchanged.
- `ManualReportProduct.SourceReportProductId` links each copied deposit/product to its previous version. `IsCorrectionChanged` tracks edited deposit rows, while `ManualReport.HasCorrectionChanges` also captures report-level changes such as employee selection, report details or payment-account changes.
- Only one open correction workspace may exist per source report. The database enforces this with the partial unique index `UX_manual_reports_open_correction_workspace`.
- User-facing “דיווח חוזר” materializes the workspace into the official Employer Interface 006 correction chain: first a full negative operation-6 cancellation of the previous current report, then a current correction. Existing unchanged rows become operation 2; edited existing rows become operation 2 or 3 according to the selected money behavior; newly added rows remain regular current rows.
- Final validation of the current correction requires the preceding negative report to have been transmitted and refreshes the previous transfer/clearing references from its feedback. Until the negative clearing identifier is available, the current correction remains blocked with an explicit validation message.
- Drafts may be hard-deleted only while editable and only if they have no transmission attempt, no feedback and no derived child report. Once any external history exists, the report remains immutable history and further changes are represented by new report/product versions.


### Correction workflow hardening
- Existing report products are edited in place inside a correction workspace so their Employer Interface metadata and payment rows are preserved instead of being cascade-deleted.
- Each changed report product carries its own optional `CorrectionOperationCode` (2 = no additional money, 3 = additional money). Deposit payment saves select operation 3 only when a positive `ActualDepositAmount` is recorded; otherwise changed existing products default to operation 2.
- Correction materialization atomically claims an eligible workspace immediately before creating the negative/current pair, preventing two operators from materializing parallel correction chains.
- Official V006 attachments are purpose-bound re-encrypted when cloning into a correction workspace and into the follow-up current correction. Negative operation-6 materialization does not inherit current-report-only attachments.


### Correction re-audit follow-up
- Operation 2/3 is explicitly supplied when editing a correction transfer; it is no longer inferred from whether an amount happened to be present. Because V006 emits one transfer per fund, the backend propagates the selected correction operation and payment details to every source-backed product in that same fund transfer.
- Correction workspace metadata/previous-reference mutation is workflow-owned. Reporting edits use `CanCreateReport`; employee-master edit permission is not required for report metadata APIs.
- Pending correction state is recalculated against the immutable source for report/payment-account/employee snapshot changes instead of trusting the historical `HasCorrectionChanges` flag, so reverting changes back to the source no longer leaves a false pending correction.
- The one-open-workspace database boundary includes `Processing`; a new workspace cannot be created while another operator is materializing the existing one.


- Correction-workspace additions are fail-closed until an official op1-within-correction flow is implemented end-to-end. The backend rejects newly added employees/products in a correction workspace and materialization rejects legacy source-less additions; modifying or removing source-backed rows remains supported.
- Pending-change comparison decrypts purpose-bound employee interface identifier, email and mobile snapshots before comparing workspace vs source, so a correction that changes only those V006 fields is detected and a true revert is not.


- Correction workspace structure is locked to the source report: employee selection and the source-product set cannot be added to or removed from. This avoids partially supported op1/new-row cases and keeps correction materialization confined to versioned edits of the immutable source graph.


- Correction previous-reference correlation and feedback transfer-ID propagation use the pension product external key when available, falling back to fund code + company name. They no longer correlate funds by `FundCode` alone, avoiding cross-manufacturer collisions while staying aligned with the reference product identity that produces the official 006 fund identifier.


- Final validation for the current correction follows the official V006 previous-reference rule: either `PreviousIdentifier`, `PreviousClearingIdentifier`, or an official exception is sufficient. It no longer waits specifically for a clearing identifier when the preceding negative report already provides a valid transfer identifier.


- Correction materialization is atomic: claiming the workspace, inserting the negative/current reports and cancelling the workspace run in one EF/PostgreSQL transaction. A failure rolls the materialization back before the workspace is marked Error, preventing orphan derived reports or a permanently Processing workspace.

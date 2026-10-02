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
- Adapters: `Alpha.Api/Services/CardComPaymentProvider.cs`, `PayPlusPaymentProvider.cs`, `PaymentProviderResolver.cs`. Provider callbacks persist a payload hash/idempotency record rather than the raw callback body; provider exception details are not returned to API callers. Payment setup is correlated to the latest pending setup reference so a late callback cannot replace a newer card; an already-active method remains active until its replacement is verified. Failed/stale webhook deliveries are retryable without acknowledging an in-flight duplicate prematurely, and anonymous callback bodies are stream-bounded to 64 KiB.
- CardCom charges use deterministic external transaction identifiers with duplicate-response replay enabled. PayPlus billing correlation uses searchable `more_info`; refunds use the provider's refund-by-original-transaction-UID endpoint.
- Billing-period charges distinguish explicit declines from ambiguous provider outcomes. Network cancellation, provider exceptions, or stale `Charging/Processing` records move to `ReconciliationRequired` and are never blindly re-charged; unsafe direct provider charges outside the billing ledger are disabled. Ambiguous refunds remain Pending so their amounts stay reserved until reconciled.
- Payment provider resolution fails closed when configuration is missing. The Fake provider requires explicit `Payments:AllowFakeProvider=true`; it is never the implicit production fallback. Test/staging merchant defaults are not shipped, PayPlus BaseUrl is required explicitly, and Invoice+ generation is opt-in.
- Billing-account responses never expose provider customer/token/mandate identifiers to the browser; only masked card metadata and safe boolean connection state are returned. Legacy manual provider-metadata write endpoints were removed so an Active payment method can only originate from the verified provider flow.
- Payment-method type changes require cancelling the existing active/default method first. Billing execution also enforces matching payment-method type and refuses suspended/cancelled accounts. Refunded and partially-refunded payments remain settled and cannot be recharged for the same period.
- Billing cancellation persists local stop state before best-effort provider cleanup, so provider downtime cannot leave ALPHA chargeable.
- Stress/audit matrix and regression scenarios: `docs/billing-payment-stress-audit.md`.
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

- Reports & Feedback exports are generated server-side from authorized report snapshots/normalized feedback as RTL XLSX workbooks: employee contribution detail, deposit summary and manufacturer feedback. Export reads never mutate submitted evidence.


- Feedback ingestion is idempotent under concurrent duplicate uploads: the unique employer+payload-hash index is the database boundary and a losing concurrent request resolves to the already-persisted feedback instead of surfacing a 500.
- Report transmission atomically claims Validated -> Processing before file reservation. After the transmission evidence row is persisted, provider dispatch and result persistence are intentionally detached from the HTTP request-abort token so a browser disconnect cannot cancel an irreversible provider side effect and leave a misleading retryable local state.

- The normalized contribution-feedback table has a dedicated `(ReportProductId, ReceivedAt)` index for the Reports & Feedback per-product modal; the broader report/product index remains for report-level reconciliation. The report deposit-list endpoint returns the report-wide distinct manufacturer set and accepts an exact manufacturer filter before paging, so the expanded Reports & Feedback grid can filter by manufacturer without missing rows outside the currently loaded page.

- Treatment updates use client-observed optimistic concurrency on `UpdatedAt`: the modal sends the version it loaded, stale operators receive a conflict even when their save starts after another operator already committed, and EF's concurrency token still protects overlapping writes. The successful update, history row and audit event remain atomic in one SaveChanges transaction. Report/feedback list indexes cover scope+updated ordering and active feedback report/transmission/contribution lookups so the operational screen does not depend on avoidable full-table scans as data volume grows.

- Clearing-house transmission fails closed when no real provider is configured. `MockReportTransmissionProvider` is opt-in (development or explicit `EmployerInterface006:AllowMockTransmission`) and is not the production default. Production V006 generation also rejects the built-in `000000000` recipient placeholder.


## Correction workspaces, delta retransmission and immutable revision history
- Sent/current business reports are immutable. Correcting one creates or reuses a single internal correction workspace linked by `ManualReport.SourceReportId`; that workspace is the complete desired next business revision, not a technical 006 file.
- User-facing **“דיווח חוזר” is report-level only**. Users may accumulate employee, product, contribution, payment-transfer, metadata and attachment edits in the same workspace, including employee/product additions and removals.
- Materialization compares the workspace with the latest effective business revision and classifies rows as Added / Changed / Removed / Unchanged. Unchanged rows are not retransmitted.
- Official Version 006 semantics drive the generated technical documents:
  - Added rows are emitted only in a current report as operation **1** (regular/current reporting).
  - Changed source-backed rows are emitted in a negative report as operation **6**, then in a current correction as operation **2** (no additional deposit) or **3** (additional deposit), according to the correction-transfer choice.
  - Removed source-backed rows are emitted only in the negative operation-6 document.
  - Operation **6** is the workbook-defined “partial or full cancellation of a transaction without refund to the employer”; therefore ALPHA does **not** cancel the whole previous report by default.
  - Operation **7** remains the official special case for correction of exempt payments only and is handled by the normal V006 rule set rather than the general 2/3 correction path.
- Current and negative technical reports are independent Employer Interface documents: current uses `SUG-MIMSHAK = 12` with operations 1/2/3/7; negative uses `SUG-MIMSHAK = 13` with operations 5/6. They must each satisfy their matching official XSD and workbook/clearinghouse rules.
- Operations 2/3/5/6/7 require previous-report correlation through `MISPAR-ZIHUI-KODEM`, `MISPAR-MISLAKA-KODEM`, or one of the official Version 006 previous-reference exceptions. Current correction validation refreshes those references from the preceding negative transmission/feedback before final export.
- V006 transfer grouping is based on fund plus operation/reference/payment semantics. A newly added operation-1 product and an operation-2/3 correction for the same fund are emitted as separate transfer blocks; products inside one transfer must share the same operation, payment data and previous-reference data.
- Payment/transfer edits are part of the delta. A change to payment method, employer/provider account, bank/branch, reference number, value date, MASAV data or other transfer-level fields marks the affected transfer/products as changed even when contribution amounts did not change.
- `ManualReportProduct.SourceReportProductId` preserves row lineage between business revisions. New rows have no source link; removed rows are absent from the desired workspace snapshot and are discovered by diff against the immutable source.
- Technical negative/current documents are linked to the workspace through `CorrectionWorkspaceId` and marked `IsTechnicalCorrectionDocument`; they are transmission evidence and are hidden from the primary Reports & Feedback business-report list.
- After every required technical document for a correction is successfully transmitted, the full workspace is promoted to the next immutable business revision (`RevisionNumber`). Future corrections may start only from the latest effective revision, keeping lineage linear: `Revision 1 -> Revision 2 -> Revision 3`.
- Only one open correction workspace may exist per source revision. `UX_manual_reports_open_correction_workspace` includes Processing, materialization runs inside one PostgreSQL transaction, and `ManualReport.UpdatedAt` is an optimistic-concurrency token so stale editors cannot resurrect or overwrite a materialized workspace.
- Pending-change counts are calculated from the actual source-vs-workspace delta, including decrypted purpose-bound employee identifier/contact snapshots, rather than trusting sticky change flags. Reverting a field to its source value removes that delta.
- Existing correction-workspace products are updated in place so Employer Interface metadata, payments and attachments retain stable workspace identity. Newly added products receive workspace identity and are reported as operation 1; removed products are physically absent from the desired workspace snapshot.
- Official V006 attachments are purpose-bound re-encrypted when cloned into correction/revision graphs. Payment confirmations remain separate immutable operational evidence and are not treated as official Employer Interface attachments.
- Drafts may be hard-deleted only while still purely internal/editable and before any transmission attempt, feedback or derived external history exists. Once external interaction exists, history remains immutable and changes are represented by a later business revision.
- Regression coverage includes PostgreSQL integration testing of a new report revision containing unchanged + changed + removed + added products. The expected delta is verified end-to-end: negative contains Changed+Removed only, current contains Changed+Added only, operation codes are 6 / 2-or-3 / 1 as applicable, the workspace promotes to Revision 2, the old revision cannot be corrected again, and Revision 3 starts from Revision 2.


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
- Core services: `EmployerInterfaceService.cs`, `EmployerInterface006ExportService.cs`, `EmployerInterface006XmlBuilder.cs`, `EmployerInterface006WorkbookRules.cs`, `EmployerInterfaceSchemaRegistry.cs`, file naming/sequence services. XML/DAT/TST report imports are bound to the selected employer registration number and exactly one salary month before persistence, preserve employee identifier type (Israeli ID/passport) into the employee master and report snapshot, and imported employer payment-account snapshots follow the same encrypted-at-rest contract as manual/Excel reporting. Correlated official clearinghouse feedback is exposed through report-feedback details for the reports UI.
- Reporting application/domain: `Alpha.Application/Reporting/*`, `Alpha.Domain/Reporting/*`.
- Persistence: `EmployerInterface006*Initializer.cs`, reporting/report lifecycle/transmission/feedback configurations and initializers.
- Tests: `EmployerInterface006*Tests.cs`, `ReportingSchemaSqlGuardTests.cs`.

## Authoritative 006 specifications
- `docs/specifications/employer-interface/006/Employer interface V 6.xlsx`.
- XSDs in the same directory.
- Clearing-house rules PDF and error-code workbook under `docs/specifications/mislaka`.
These sources outrank assumptions, old examples and UI behavior.

- V006 export groups by fund; official workbook validation rejects inconsistent per-product transfer/payment details within a fund rather than silently selecting a conflicting payment.
- Deposit list account resolution: `ManualReportEndpoints.GetDepositsAsync` resolves the current reference fund bank details when a draft has no persisted payment entry, so the list and payment editor agree.
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

- Draft employee postal corrections use tenant-scoped PATCH `/manual-reports/{id}/employees/{employeeId}/postal-address`; the employee master and unrelated encrypted snapshot stay unchanged. Preflight checks official XSD address bounds (MIKUD <=7 digits; TA-DOAR 0..99999) before XML schema validation and names the affected report employee.

- Editing a person's postal code/post-office box in their employee card updates matching address snapshots in still-editable reports for the same authorized employment, in the same transaction. Imported/overridden snapshots and immutable submitted/sent reports remain untouched; refreshed drafts are marked dirty and revalidated. Address fields have no duplicate editor inside the report wizard.

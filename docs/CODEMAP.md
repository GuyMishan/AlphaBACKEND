# ALPHA Backend code map

## Solution layers
- `src/Alpha.Api` — HTTP endpoints, auth, middleware, provider adapters and hosted API services.
- `src/Alpha.Application` — authorization, billing, entitlements, identity and reporting orchestration.
- `src/Alpha.Domain` — domain entities and financial/reporting models.
- `src/Alpha.Infrastructure` — EF Core, schema initialization and persistence configuration.
- `src/Alpha.Worker` — background worker boundary.
- `tests/*` — API/domain tests.

## Authentication, users and access
- API: `Authentication/*`, `Endpoints/AuthEndpoints.cs`, `AccessEndpoints.cs`, `InvitationEndpoints.cs`, `ScopeEndpoints.cs`.
- Application: `Authorization/OrganizationAccessService.cs`, `Identity/InvitationService.cs`.
- Domain: `Identity/*`, `Organizations/OrganizationMembership.cs`, `Employers/EmployerUserAccess.cs`.
- Persistence: identity/access/invitation schema initializers.

## Organizations, employers and employees
- Endpoints: `OrganizationEndpoints.cs`, `EmployerEndpoints.cs`, profile endpoints, employee pension mix endpoints.
- Domain: `Organizations/*`, `Employers/*`, `Employees/*`.

## Billing, entitlements and payment providers
- Endpoints: `BillingAccountEndpoints.cs`, `BillingManagementEndpoints.cs`, `PaymentProviderEndpoints.cs`.
- Application: `Billing/*`, `Entitlements/*`.
- Provider boundary: `Alpha.Application/Billing/IPaymentProvider.cs`.
- Adapters: `Alpha.Api/Services/CardComPaymentProvider.cs`, `PayPlusPaymentProvider.cs`, `PaymentProviderResolver.cs`. Provider callbacks persist a payload hash/idempotency record rather than the raw callback body; provider exception details are not returned to API callers.
- Persistence: `Billing*SchemaInitializer.cs`, `BillingConfigurations.cs`.
- Free-plan quota mutations are serialized per organization by `Alpha.Infrastructure/Persistence/OrganizationEntitlementLock.cs`; guarded writes require an explicit transaction commit and otherwise roll back; flows that already own a transaction (such as invitation acceptance during registration) join the same organization advisory lock. PostgreSQL concurrency/rollback coverage lives in `OrganizationEntitlementLockConcurrencyTests.cs`.

## Employer Interface 006 / reporting
- Endpoints: `EmployerInterfaceEndpoints.cs`, `ManualReportEndpoints.cs`, `ReportValidationEndpoints.cs`, `ReportTransmissionEndpoints.cs`, `ReportFeedbackEndpoints.cs`, `DerivedReportEndpoints.cs`.
- Transmission endpoints suppress provider/internal exception details from client-visible responses. Employer Interface 006 transmission evidence, clearinghouse feedback XML and report PDF attachments are encrypted at rest with purpose binding; attachments are decrypted only at download/transmission boundaries while their evidence hash/size remain bound to the original plaintext. The startup sensitive-data backfill encrypts legacy plaintext evidence idempotently. The current access model still maps report creation/transmission to the existing employee-create permission; introducing dedicated report permissions requires a coordinated membership/API/frontend permission-schema migration.
- Core services: `EmployerInterfaceService.cs`, `EmployerInterface006ExportService.cs`, `EmployerInterface006XmlBuilder.cs`, `EmployerInterface006WorkbookRules.cs`, `EmployerInterfaceSchemaRegistry.cs`, file naming/sequence services.
- Reporting application/domain: `Alpha.Application/Reporting/*`, `Alpha.Domain/Reporting/*`.
- Persistence: `EmployerInterface006*Initializer.cs`, reporting/report lifecycle/transmission/feedback configurations and initializers.
- Tests: `EmployerInterface006*Tests.cs`, `ReportingSchemaSqlGuardTests.cs`.

## Authoritative 006 specifications
- `docs/specifications/employer-interface/006/Employer interface V 6.xlsx`.
- XSDs in the same directory.
- Clearing-house rules PDF and error-code workbook under `docs/specifications/mislaka`.
These sources outrank assumptions, old examples and UI behavior.

## Security
- `src/Alpha.Api/Security/*` — versioned AES-GCM string/binary encryption and legacy-compatible sensitive-data backfill, headers, audit, malware scanning, retention and session activity. `SessionActivityMiddleware` rejects revoked/expired/idle sessions and sessions whose user has been deactivated. `/api/auth/logout` revokes the current server-side session, so the associated JWT cannot continue through the middleware. Login OTP requests use a neutral unknown-account response, and OTP HMAC material is domain-separated from JWT signing.
- `src/Alpha.Domain/Auditing/AuditEvent.cs`.

## Database
- `AlphaDbContext.cs` / `AlphaDbContextFactory.cs`.
- `EntityConfigurations.cs`, `ReportingEntityConfigurations.cs` and focused schema initializers.
- Check existing initializer/configuration patterns before introducing a new table or column.

## CI
- `.github/workflows/ci.yml` restores, Release-builds and tests the full solution.

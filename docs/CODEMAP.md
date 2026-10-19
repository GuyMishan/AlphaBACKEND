# Alpha code map

Where to find things. Paths are relative to the repository root. For the rules behind this layout see [architecture.md](architecture.md).

## Start here

| Question | Location |
|---|---|
| How is the process composed? | `src/Alpha.Api/Program.cs` |
| Where is route X implemented? | `rg 'Map(Get|Post|Put|Delete|Group)\("…' src/Modules` → `src/Modules/<Module>/Alpha.Modules.<Module>/Features/<Capability>` |
| How is a module registered? | `src/Modules/<Module>/Alpha.Modules.<Module>/<Module>Module.cs` |
| Where are entities and invariants? | `src/Alpha.Domain/<Area>` |
| Where are EF mappings and DDL? | `src/Alpha.Infrastructure/Persistence` |
| Where is authorization decided? | `src/Modules/IdentityAccess/Alpha.Modules.IdentityAccess/Application/Authorization/OrganizationAccessService.cs` |
| Where are cross-module contracts? | `src/Modules/*/Alpha.Modules.*.Contracts` |
| Which module owns table X? | `tests/Alpha.Architecture.Tests/ModuleOwnershipTests.cs`, [database.md](database.md#inventory-and-ownership) |

## Host — `src/Alpha.Api`

| File | Responsibility |
|---|---|
| `Program.cs` | Registration, database initialization, middleware order, module and shared route-group mapping |
| `Authentication/AuthenticationSetup.cs` | JWT / Development scheme, `ICurrentUser`, fallback authorization policy |
| `Authentication/CurrentUser.cs`, `DevelopmentHeaderAuthentication.cs` | Claims → `ICurrentUser`; Development-only header identity |
| `Security/ReverseProxySetup.cs` | Opt-in trusted forwarded headers (client IP, scheme); first middleware |
| `Security/FrontendProxyClientAddress.cs` | Client IP attested by the Next.js frontend server with `FrontendProxy:SharedSecret` (applied inside `UseAlphaReverseProxy`) |
| `Security/SessionActivityMiddleware.cs` | Server-side session, active user and admin-claim validation |
| `Security/SecurityHeadersMiddleware.cs`, `RateLimitingSetup.cs` | Response headers; rate-limit partitions |
| `Security/AesDataProtectionService.cs` | `IDataProtectionService` (AES-GCM + HMAC lookup hashes) |
| `Errors/ApiErrors.cs`, `ApiErrorResultFilter.cs`, `ApiExceptionHandler.cs`, `ErrorHandlingSetup.cs` | The single error contract (RFC 9457 + `errorCode`, `traceId`, `errors`): codes and enrichment, conversion of module results, exception mapping ([architecture.md](architecture.md#error-handling)) |
| `Security/StartupConfiguration.cs` | Fail-fast validation of critical settings after build |
| `Security/SensitiveDataBackfill.cs`, `SensitiveDataBackfillHostedService.cs` | Bounded, coordinated legacy-plaintext encryption backfill (runs until a clean pass) |
| `Payments/*` | CardCom, PayPlus, Fake providers, resolver, `AddPaymentProviders` |
| `GlobalUsings.cs` | Contracts implemented by host adapters |

## Shared projects

- `src/Alpha.Domain`: `Auditing/AuditEvent`, `Billing/*`, `Common/Entity`, `Common/IsraeliNationalId`, `Employees/*`, `Employers/*`, `Identity/*`, `Organizations/*`, `Reporting/*`, `Validation/WorkforceInputValidation`.
- `src/Alpha.Infrastructure`: `DependencyInjection.cs` (DbContext, advisory lock, connection-string normalization); `Persistence/AlphaDbContext.cs`; `Persistence/*Configuration(s).cs`; `Persistence/AlphaDatabaseInitializer.cs` (ordered startup DDL, including `BillingPendingSetupSchemaInitializer` for card setup drafts); `Persistence/*Initializer.cs` (incl. `EmployerInterface006FieldMapInitializer`, which loads the official V6 workbook); `Persistence/OrganizationEntitlementLock.cs`; `Persistence/DataFixMarkers.cs` (completion markers of one-time jobs); `Persistence/DemoDataSeeder.cs`.

## Modules — `src/Modules`

### IdentityAccess

`Features/Authentication` (OTP login/registration, logout, session), `AccessManagement` (memberships and employer grants), `Invitations`, `Referents`, `Scope`, `UserPreferences`, `PlatformAdministration` (platform users, attached to `/api/platform`). `Application/Authorization/OrganizationAccessService.cs`, `Application/Invitations/InvitationService.cs`. `Infrastructure/Delivery/OtpDelivery.cs`, `Infrastructure/Retention/SecurityRetentionHostedService.cs`. Contracts: `ICurrentUser`, `IOrganizationAuthorization`, `IDataProtectionService`.

### Organizations

`Features/Management` (organizations, memberships API), `ProfileCenter`, `Dashboard`, `Onboarding`, `EmployerTransfers` (`EmployerTransferService` is the reference atomic cross-module operation).

### Workforce

`Features/EmployerManagement` (employers, employees, search, status; uses Billing and Reporting contracts), `EmployerProfile`, `EmployeePensionMix`.

### Billing

`Features/Accounts`, `Features/Management` (`BillingManagementEndpoints` partial class: `.Platform.cs` platform-admin operations, `.Tenant.cs` organization/employer self-service), `Features/PaymentMethods` (staged billing-details setup/sync/cancel, provider callbacks which commit details only after verified approval). `Application/`: `BillingEngine` (calculator, usage collector), `BillingCycleService` + `BillingCycleHostedService`, `BillingGateService`, `EntitlementService`, `BillingInheritanceService`. Contracts: entitlements, inheritance, transmission gate, payment providers.

### Reporting

| Folder | Content |
|---|---|
| `Features/ManualReports` | Reports, employees, deposits, selection; `ReportingInputValidationMiddleware` |
| `Features/Attachments`, `PaymentConfirmations`, `PaymentAccounts` | Report documents (incl. copy from the library), payment evidence, bank accounts |
| `Features/EmployerDocuments` | Employer/product 006 document library (types 3–6) |
| `Features/Profiles`, `PreviousReferences` | Employer/employee 006 profiles, previous-report references |
| `Features/EmployerInterface006` | XML builder/serializer, workbook rules, Excel, naming, schema registry, preflight, export |
| `Features/Validation` | Report validation, validation export, shared rules |
| `Features/Import` | 006 import (`EmployerInterfaceService`); `LegacyFeedbackNormalization` (one-time startup backfill) |
| `Features/Corrections` | Derived reports, `CorrectionWorkflowService` |
| `Features/Transmissions` | Send/history, hybrid dispatcher, manufacturer routing, file sequences |
| `Features/FeedbackIngestion` | Line feedback parser |
| `Features/Feedback` | `ReportFeedbackEndpoints` partial class: `.cs` (routes, shared helpers, requests), `.Queries`, `.Resolution`, `.ExternalCases`, `.Documents`, `.Treatments`, `.Exports`; status resolver, attempt selection |
| `Features/PublicReferenceData` | 006 catalogs on `/api/reference-data` |
| `Application/` | Payment-account service, draft snapshots, usage facts, feedback-resolution playbook/matcher |
| `Infrastructure/` | Evidence storage, malware scanner; `Transmissions/ClearinghouseProviderSelection` (the explicit `Simulated`/`Real`/`Mock`/`Disabled` switch) with the mock and refusing providers; `Simulation/` (TEST clearinghouse simulator and manufacturer vaults) |

Business rules: [reporting.md](reporting.md). Authoritative 006 sources: `docs/specifications/employer-interface/006` and `docs/specifications/mislaka`. XSDs are copied to `Specifications/EmployerInterface/006` in build, publish and the container.

### ReferenceData

`Features/Administration` (platform sync and admin catalog queries), `Features/PublicCatalogs` (options, salary layers, cities, streets, banks, branches, pension funds and products), `Application/ReferenceDataSyncService.cs`.

### Auditing

`Middleware/SecurityAuditMiddleware.cs` (correlation ID, audit rows for mutations and file/export reads), `Features/SecurityEvents/AuditingEndpoints.cs` (`GET /api/platform/security/events`).

## Tools

`tools/Alpha.SchemaVerifier`: `SchemaCatalog` (read-only `pg_catalog` capture), `SchemaComparer` (Blocking/Review/Info classification), `Program` (`expected`, `capture`, `compare`). Usage: [database.md](database.md#production-schema-verification).

`tools/production-readonly/`: `data-checks.sql` (read-only production marker and plaintext counts, same scope as the backfill recount) and `grants.sql` (the least-privilege login it needs). Procedure and approval: [release.md](release.md#production-read-only-verification).

`integrations/google-apps-script/Code.gs`: the mail gateway (login/registration codes, invitation links), deployed by hand as a Google Apps Script web app; called by `OtpDelivery` in IdentityAccess.

## Tests

- `tests/Alpha.Architecture.Tests`: `ProjectDependencyTests`, `HostBoundaryTests`, `ModuleOwnershipTests`, `CompatibilityBaselineTests`, `MailGatewayContractTests`, `Repository` helper.
- `tests/Alpha.Api.Tests/<Module>`: module behavior and HTTP characterization (`Organizations/EmployerTransfer*`, `Reporting/ReportingHttpCharacterizationTests`, `Billing/BillingPaymentFlowIntegrationTests`, `IdentityAccess/IdentityAccessHttpIntegrationTests`, ...).
- `tests/Alpha.Api.Tests/Security`: `AuthorizationSecurityTests`, `DataIntegrityRollbackTests`, `ReverseProxySecurityTests`, `SensitiveDataBackfillTests`, `OtpEnumerationTests`, `InvitationLinkTests`, `PaymentCallbackUrlTests`, `StartupConfigurationTests`, data-protection and concurrency handler tests.
- `tests/Alpha.Api.Tests/Integration`: `ModuleHttpIntegrationTests`, `ApiErrorContractTests`, `DatabaseBootstrapIntegrationTests`, `EfModelBaselineTests`, `SchemaVerifierTests`, `ProductionReadOnlyChecksTests`.
- `tests/Alpha.Api.Tests/TestInfrastructure`: `AlphaApiTestHost`, `TestData`, `PostgresTestEnvironment`, `RepositorySource`, `RecordingMailGateway` (stands in for the mail gateway).
- `tests/Alpha.Domain.Tests`: domain invariants and `IsraeliNationalIdTests`.

## Trace a request

1. Find the route literal under `src/Modules`.
2. Read the authorization call and the tenant-scoped query in the handler.
3. Follow injected services within the module; cross-module calls go through a Contracts interface.
4. Inspect the entity in `Alpha.Domain`, then its configuration and initializer in `Alpha.Infrastructure/Persistence`.
5. Find the matching tests under `tests/Alpha.Api.Tests/<Module>` and `Security`.

## Legal document foundation

- `src/Modules/ReferenceData/Alpha.Modules.ReferenceData/Features/LegalDocuments/LegalDocumentEndpoints.cs`: public active and historical legal Markdown, platform-admin draft/publish/archive API.
- `src/Alpha.Infrastructure/Persistence/LegalDocumentSchemaInitializer.cs`: additive global document-version schema. This is intentionally managed through raw SQL (no new DbSet) so the established EF model remains unchanged.

- `docs/legal/ALPHA_LEGAL_POLICY_V1.md` — version 1 published legal text; embedded by `src/Alpha.Infrastructure/Alpha.Infrastructure.csproj`, seeded by `LegalDocumentSchemaInitializer.PublishVersionOneIfEmptyAsync` only on an empty store.

## Employer agreement transmission prerequisite

Employer agreement is document type 7 in the existing Reporting/EmployerDocuments API and is employer-wide (no employment/product). Reporting/Transmissions/ReportTransmissionEndpoints verifies its existence for both initial sends and hybrid resume before any provider dispatch. The Frontend reuses EmployerDocumentsTab and the common tooltip.

## Registration and legal reacceptance

IdentityAccess binds legal version and marketing opt-in to registration OTP through RegistrationConsentSchemaInitializer. UserPreferences owns `/api/me/preferences/marketing`. AuthEndpoints exposes `/api/auth/legal/accept` and `/api/auth/session` reports `legalConsentRequired`. SessionActivityMiddleware enforces current legal acceptance in Production and permits only session, logout, consent and public legal reading while consent is outstanding.

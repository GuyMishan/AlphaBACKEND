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
- Adapters: `Alpha.Api/Services/CardComPaymentProvider.cs`, `PayPlusPaymentProvider.cs`, `PaymentProviderResolver.cs`.
- Persistence: `Billing*SchemaInitializer.cs`, `BillingConfigurations.cs`.

## Employer Interface 006 / reporting
- Endpoints: `EmployerInterfaceEndpoints.cs`, `ManualReportEndpoints.cs`, `ReportValidationEndpoints.cs`, `ReportTransmissionEndpoints.cs`, `ReportFeedbackEndpoints.cs`, `DerivedReportEndpoints.cs`.
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
- `src/Alpha.Api/Security/*` — encryption/data protection, headers, audit, malware scanning, retention and session activity.
- `src/Alpha.Domain/Auditing/AuditEvent.cs`.

## Database
- `AlphaDbContext.cs` / `AlphaDbContextFactory.cs`.
- `EntityConfigurations.cs`, `ReportingEntityConfigurations.cs` and focused schema initializers.
- Check existing initializer/configuration patterns before introducing a new table or column.

## CI
- `.github/workflows/ci.yml` restores, Release-builds and tests the full solution.

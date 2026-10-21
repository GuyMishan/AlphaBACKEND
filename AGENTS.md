# ALPHA Backend agent guide

## Purpose
ALPHA backend is a .NET 10 feature-first modular monolith for multi-tenant pension operations: one ASP.NET Core host (`Alpha.Api`), business modules under `src/Modules`, a shared domain model (`Alpha.Domain`) and one `AlphaDbContext` (`Alpha.Infrastructure`) over PostgreSQL. Hierarchy: Platform -> Organization -> Employer -> Employee/Employment.

## Before changing code
1. Read `docs/CODEMAP.md` and `docs/architecture.md`; for security-relevant work also `docs/security.md`, for schema work `docs/database.md`, for reporting/006 work `docs/reporting.md`.
2. Trace the full request path: module endpoint -> authorization -> module service -> domain/persistence -> provider.
3. For Employer Interface 006 work, read the authoritative files under `docs/specifications/employer-interface/006` and `docs/specifications/mislaka`; do not infer schema rules from UI behavior.

## Architecture rules
- New features go in the owning module under `Features/<Capability>`, registered and mapped from `<Module>Module.cs`. `Alpha.Api` stays composition and host infrastructure only; do not add endpoints there.
- A module may reference another module only through its `.Contracts` project. Contracts projects stay dependency-free. Never reference `Alpha.Api` from a module.
- `Alpha.Domain` has no dependencies; `Alpha.Infrastructure` depends only on Domain. Keep one `AlphaDbContext`; do not add repositories, `IAlphaDbContext`-style abstractions or extra DbContexts.
- Using another module's `DbSet` fails `ModuleOwnershipTests` until the access is reviewed and added to its allow-list with a reason.
- Do not reintroduce `Alpha.Application`, `Alpha.Worker` or `Alpha.Modules.Auditing.Api`.

## Security and data rules
- Call `IOrganizationAuthorization` for the specific organization/employer before any read or write, and filter every query by both route IDs. Client-supplied IDs are selectors, never authorization.
- Return explicit response records; never serialize entities with encrypted values or lookup hashes.
- Report failures with `Results.*` (an established snake_case `error` code is fine); the host turns every error into the single Problem Details contract (`docs/architecture.md#error-handling`). Modules never write error bodies themselves.
- Build links that go into e-mails or to providers from configuration (`Frontend:BaseUrl`, `Payments:PublicApiBaseUrl`), never from `Origin`/`Host` headers. Anonymous endpoints must answer identically whether or not an account exists.
- Multi-step writes run in one transaction (`ExecuteUpdate/Delete` commit immediately on their own). Use `OrganizationEntitlementLock` for limit checks and last-admin style invariants.
- Audit events are append-only; submitted financial/reporting evidence is immutable or superseded/reversed, never rewritten.
- Payment provider code stays behind `IPaymentProvider`/`IPaymentProviderResolver`; transmission behind `IReportTransmissionProvider`.
- Do not change encryption formats, purpose strings, routes, request/response contracts, the EF model or schema initializers without an explicit, documented decision; the baselines in the tests will fail and must be updated together with `docs/database.md` or the release notes.
- Never log national IDs, bank data, report payloads, credentials or tokens.

## Safety
- Never connect to production, staging or shared databases. Tests run only against `docker-compose.test.yml` (loopback, `alpha_tests`).
- A push or merge to `main` deploys to production. Do not commit, push or deploy without explicit approval.

## Verification
Before considering a backend change complete run:
```bash
docker compose -f docker-compose.test.yml up -d --wait
export ALPHA_TEST_POSTGRES="Host=127.0.0.1;Port=55432;Database=alpha_tests;Username=alpha_test;Password=alpha_test_only"
dotnet restore AlphaBackend.slnx
dotnet build AlphaBackend.slnx --no-restore --configuration Release
dotnet test --solution AlphaBackend.slnx --no-build --configuration Release
```
Report skipped tests as skipped. Do not claim verification passed unless it was run or CI confirms it.

## Documentation — part of every change
Documentation maintenance is part of implementation, not a separate optional task. Before completing any change, check whether it changed architecture, file ownership, a major flow, API/integration behavior, security controls, the schema, verification commands, setup, or shared conventions.

- Update `docs/CODEMAP.md` when files/flows move, a subsystem is added, or module ownership changes.
- Update `docs/architecture.md` when boundaries, contracts, cross-module flows or approved exceptions change.
- Update `docs/security.md` when an authentication/authorization/data-protection control or a known risk changes.
- Update `docs/database.md` for any schema, initializer or ownership change; `docs/reporting.md` for Employer Interface 006 business rules.
- Update `README.md` / `docs/development.md` when setup, configuration or verification instructions change.
- Do not churn documentation for trivial internal refactors.
- A change that requires documentation is not complete until the relevant Markdown is updated in the same work.

## Simulation testing flag — reporting conversations

- The configuration key `EmployerInterface006:SimulationTestMode` is set to `true` (ON) in `src/Alpha.Api/appsettings.json` for planned isolated simulation tests. In any reporting-related status update, explicitly state the configured flag value and whether the bypass route is actually implemented/enabled.
- **A configured `true` flag alone does not bypass any validation**. The isolated bypass path is not implemented yet. Do not represent this flag as proof that invalid reports can be transmitted.
- Any future bypass implementation must be restricted to an independently isolated TEST runtime, `EnvironmentCode=1`, simulated providers only, and explicit test-report endpoints; it must not relax standard report endpoints, authorization, tenant isolation, financial evidence immutability, or real transmission protections. Fail closed when isolation cannot be established.

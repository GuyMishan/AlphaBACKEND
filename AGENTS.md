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
- Audit events are append-only. Real submitted financial/reporting evidence is immutable or superseded/reversed, never rewritten. For **explicitly verified demo-only** reports sent exclusively to simulated providers, controlled cleanup/deletion is allowed under the demo cleanup exception below.
- Payment provider code stays behind `IPaymentProvider`/`IPaymentProviderResolver`; transmission behind `IReportTransmissionProvider`.
- Do not change encryption formats, purpose strings, routes, request/response contracts, the EF model or schema initializers without an explicit, documented decision; the baselines in the tests will fail and must be updated together with `docs/database.md` or the release notes.
- Never log national IDs, bank data, report payloads, credentials or tokens.

## Safety
- Development and automated tests must only use `docker-compose.test.yml` (loopback, `alpha_tests`); no incidental direct writes to production, staging or shared databases. A user-explicitly-requested cleanup of **verified demo-only data** in the shared ALPHA database is an exception only under the safeguards below.
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

- Always report `EmployerInterface006:SimulationTestMode` in reporting discussions: configured **ON** (`true` in `src/Alpha.Api/appsettings.json`).
- When flag ON, `EnvironmentCode=1` and `ClearinghouseProvider=Simulated`, the dedicated `/simulation/payload` and `/simulation/transmissions` routes are enabled **also when the host environment is Production**. These are explicit tenant-scoped simulation routes, not overrides of the normal `/transmissions` endpoint.
- Simulation transmission accepts only unsubmitted Draft (non-correction-workspace) current/negative reports, enforces access, and routes only to `SimulatedVault`. Product routes to other providers fail closed. Existing standard transmission validation is unchanged.
- The simulation flag is NOT an entitlement to use an arbitrary database safely: in the deployed host, simulated report/transmission/feedback writes use the configured database. Use dedicated test employers and do not use real financial records. The simulator remains scenario-driven (`success` default), not yet data-derived.
- No verification should be claimed unless CI/build/tests or Render live status is observed.

## Explicit demo report deletion exception (2026-10-21)

- When the user explicitly requests deletion/reset of a **demo report**, including one already transmitted, agents MAY implement and use a controlled cleanup mechanism to remove that report and its **exclusively owned** dependent records, even when the demo instance is hosted under a Production ASP.NET environment. This is intentionally allowed to unblock sequential Employer Interface 006 tests.
- Preconditions: `EmployerInterface006:SimulationTestMode=true`, `EmployerInterface006:EnvironmentCode=1`, `EmployerInterface006:ClearinghouseProvider=Simulated`; verify exact report ID, organization/employer ownership and that **all** transmissions are to simulated providers. A generic flag, simulated environment code, employer name or month alone is not evidence that a report is demo-only. Reject mixed real/simulated transmission histories or any ambiguous match.
- Prefer a tenant-authorized, auditable backend **demo-cleanup endpoint/service** with explicit report ID and a transaction over direct database SQL. Keep the existing normal delete endpoint restrictions for real reports. Discover and clear dependent contribution, product, employee, payment, attachment, feedback, resolution and transmission records without touching shared employee master data, employer accounts, other reports or audit events. Handle external vault files/storage with an explicit safe orphan cleanup policy. Make the operation idempotent and verify postconditions.
- If cleanup is requested against the shared database and prerequisites cannot be independently verified, do not destroy rows merely on the user's characterization of the environment; describe what remains blocked. Never print credentials or PII. A logical demo reset / hiding action is acceptable if historical foreign keys cannot be safely removed.
- Explicit user authorization for a named demo report permits executing its verified cleanup; no additional permission to delete unrelated records is implied. Record stable identifier, count of affected rows by table, and an audit event. Normal transmitted reports remain immutable.

## Saved manual demo report cleanup SQL

- Reusable, guarded SQL: `scripts/maintenance/demo-report-cleanup.sql`. Consult it when the user explicitly requests resetting demo reports, rather than recreating ad hoc SQL.
- It contains the previously tested Guy demo-employer report IDs as examples. **Update the exact employer and target report IDs for every new operation**; the saved IDs must never be taken as a blanket deletion authorization.
- Script defaults to **ROLLBACK** (dry run). Back up the target database, inspect the selected rows, verify purely simulated transmissions and related storage implications, and use **COMMIT** only following explicit deletion authorization and successful preflight. The SQL does not clean external vault/storage files or create application audit events. Prefer audited backend cleanup functionality when available.

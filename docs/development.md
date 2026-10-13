# Developing Alpha

## Prerequisites

- .NET SDK 10 (pinned in `global.json`)
- Docker Desktop (local PostgreSQL 17)

## First run

```powershell
docker compose up -d postgres                       # localhost:5432, database "alpha", persistent volume
dotnet user-secrets --project src/Alpha.Api set "Security:DataProtectionKey" "<32-byte base64 key>"
dotnet run --project src/Alpha.Api                  # http://localhost:5080/swagger
```

Generate a local key with `[Convert]::ToBase64String((1..32 | % { Get-Random -Max 256 }) -as [byte[]])` (PowerShell). The key protects every encrypted column; data written with one key cannot be read with another.

The `http` launch profile runs in Development. On an empty database startup creates the EF model and then applies the 31 schema initializers (`Database:BootstrapEmptyDatabase` defaults to true in Development). An existing database is only upgraded idempotently. See [database.md](database.md).

### Authentication in Development

Development registers a header scheme instead of JWT validation:

- `X-User-Id`: user UUID
- `X-Session-Id`: an existing, unexpired `identity.user_sessions` row of that user
- `X-Platform-Admin: true`: platform-admin claim (rejected unless `identity.users.IsPlatformAdmin` is true)

The session middleware validates the session exactly as in production, so headers without a valid session get `401`. For local testing, insert a user and a session row (for example with `psql`) or seed demo data (`PrototypeAuth:SigningKey` plus `DemoData:Enabled=true`). To exercise the real OTP and JWT flow, run with a non-Development environment name and `PrototypeAuth:SigningKey` set.

### Configuration

Use environment variables (`Section__Key`) or user-secrets; never commit secrets. `src/Alpha.Api/appsettings.json` contains placeholders only.

| Key | Purpose |
|---|---|
| `ConnectionStrings:AlphaDatabase` | PostgreSQL connection (keyword form or `postgres://` URL) |
| `Security:DataProtectionKey` | AES-256 key, base64, required |
| `PrototypeAuth:SigningKey` / `Authentication:Authority`, `Audience` | Token validation outside Development |
| `Database:BootstrapEmptyDatabase` | Create the EF model on an empty database (default: Development only) |
| `Security:EnableSensitiveDataBackfill`, `Security:SensitiveDataBackfill:BatchSize`/`BlobBatchSize` | Background legacy-plaintext encryption pass (default on; stops after a clean pass; see [security.md](security.md#sensitive-data)) |
| `ReverseProxy:Enabled`, `KnownNetworks`, `KnownProxies`, `ForwardLimit`, `DiagnosticRequestCount` | Trusted forwarded headers behind a proxy (default off; see [release.md](release.md#client-ip-behind-render)) |
| `FrontendProxy:SharedSecret` | Secret (at least 32 bytes) shared with the Next.js server; when set, its `X-Alpha-Client-IP` header is the client address for rate limits and audit (default off; see [release.md](release.md#client-ip-through-the-frontend)) |
| `Payments:*` | Provider selection (`DefaultProvider`), CardCom/PayPlus credentials, `AllowFakeProvider` |
| `Billing:*` | Automatic billing job (`AutomaticBillingEnabled` default false) and retry policy |
| `EmployerInterface006:ClearinghouseProvider` | `Simulated` (default), `Mock` (Development), `Disabled` or `Real` (not implemented); validated at startup, see [reporting.md](reporting.md#clearinghouse-provider-simulated-or-real) |
| `EmployerInterface006:*` | Sender/recipient identity, environment code (1 = TEST), simulator settings (`SimulatedVault:*`) |
| `Reporting:ManufacturerRouting:*` | Manufacturer routing table and aliases |
| `Otp:*`, `Storage:*`, `Security:MalwareScanner:*` | OTP delivery (`Otp:Email:AppsScript:Url`/`Secret` for the mail gateway), payment-evidence storage, malware scanner |
| `Frontend:BaseUrl` | Public frontend URL used in invitation links and payment return URLs; set it locally (for example `http://localhost:5173`), otherwise links point at the production frontend |
| `Payments:PublicApiBaseUrl` | Public API URL given to payment providers for callbacks; absolute `https` (`http` only for localhost). Unset: `RENDER_EXTERNAL_URL` (Render); neither valid: payment-method setup returns 503 |

With the default `ClearinghouseProvider=Simulated`, a local `dotnet run` from the repository root writes simulator files under `simulated-clearinghouse-vault/` (the path is relative to the working directory). Test hosts select `Mock` through `UseSetting`, because settings added with `ConfigureAppConfiguration` are applied only after `Program.cs` has registered services.

## Build and test

```powershell
dotnet restore AlphaBackend.slnx
dotnet build AlphaBackend.slnx --no-restore --configuration Release   # warnings are errors
dotnet test --solution AlphaBackend.slnx --no-build --configuration Release
```

### PostgreSQL test database

PostgreSQL-backed tests are skipped (and reported as skipped) unless `ALPHA_TEST_POSTGRES` is set. A run with skips is fine for quick feedback but is not verification.

```powershell
docker compose -f docker-compose.test.yml up -d --wait
$env:ALPHA_TEST_POSTGRES = "Host=127.0.0.1;Port=55432;Database=alpha_tests;Username=alpha_test;Password=alpha_test_only"
dotnet test --solution AlphaBackend.slnx --no-build --configuration Release
docker compose -f docker-compose.test.yml down
```

The container stores data in `tmpfs` and listens on loopback only. `PostgresTestEnvironment` rejects any connection string whose host is not loopback or whose database is not `alpha_tests`; tests then create and drop their own databases on that server. CI (`.github/workflows/ci.yml`) runs the same commands with a PostgreSQL service.

### Test suites

| Project / folder | Covers |
|---|---|
| `Alpha.Architecture.Tests` | project graph, module/host boundaries, DbSet ownership allow-list, route/table/initializer baselines, middleware order, Dockerfile restore layer (source only, no production assembly loaded) |
| `Alpha.Domain.Tests` | entity invariants, national-ID check |
| `Alpha.Api.Tests/<Module>` | per-module behavior and HTTP characterization |
| `Alpha.Api.Tests/Security` | cross-tenant denial, privilege escalation, sessions, anonymous allow-list, rollback under injected failures |
| `Alpha.Api.Tests/Integration` | cross-module HTTP flows, database bootstrap/upgrade, EF model baseline |
| `Alpha.Api.Tests/TestInfrastructure` | `AlphaApiTestHost` (in-process API on a fresh database with header identities and real session validation; optional service overrides), `TestData`, `PostgresTestEnvironment`, `RepositorySource`, `RecordingMailGateway` |

Writing a security test: use `AlphaApiTestHost.RunAsync`, seed tenants with `TestData`, send requests with `AlphaApiTestHost.Request(...)`, and assert both the status code and that nothing changed in the database. Failure injection uses a trigger created only inside the test database (see `DataIntegrityRollbackTests`).

Use synthetic data only. Never point tests at a real database.

### Production schema verification

`tools/Alpha.SchemaVerifier` builds the expected schema on the local test server and compares it with a read-only catalog capture of another database. Usage: [database.md](database.md#production-schema-verification). Running it, or `tools/production-readonly/data-checks.sql`, against production follows the approved procedure in [release.md](release.md#production-read-only-verification).

## Publish and container

```powershell
dotnet publish src/Alpha.Api/Alpha.Api.csproj -c Release -o out
docker build -t alpha-api:local .
```

The Employer Interface 006 XSDs are copied to `Specifications/EmployerInterface/006` in build, publish and the image. When you add a project to the API graph, add its `.csproj` to the Dockerfile restore layer; `HostBoundaryTests.Dockerfile_restore_layer_copies_every_project_the_api_depends_on` enforces it.

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `42P01 relation ... does not exist` at startup | Empty database with bootstrap disabled. Set `Database:BootstrapEmptyDatabase=true` for a local database, or point at an initialized one. |
| `Invalid configuration: ...` at startup | `StartupConfiguration` lists the missing or invalid settings (data-protection key; outside Development the token signing key or authority). |
| Every request returns 401 in Development | Missing or invalid session: headers need a matching `UserSession` row. |
| PostgreSQL tests all skipped | `ALPHA_TEST_POSTGRES` not set in the shell running `dotnet test`. |
| `Safety guard rejected ALPHA_TEST_POSTGRES` | Host is not loopback or database is not `alpha_tests`. This is intentional. |
| Container build `NETSDK1004 assets file not found` | A project in the API graph is missing from the Dockerfile restore layer. |
| Architecture test about foreign DbSet usage | You touched another module's table; use its Contracts or get the access reviewed (see [architecture.md](architecture.md#data-ownership-inside-the-shared-dbcontext)). |

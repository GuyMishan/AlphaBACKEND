# Alpha Backend

Alpha is a multi-tenant pension-operations backend for the hierarchy Platform → Organization → Employer → Employee/Employment: identity and access, workforce data, billing, pension reporting, Employer Interface 006, transmissions, clearinghouse feedback and audit.

It is a .NET 10 feature-first modular monolith: one ASP.NET Core API, one deployment unit, one PostgreSQL database accessed through a single `AlphaDbContext`.

## Technology

- .NET 10, ASP.NET Core Minimal APIs
- EF Core 10 with PostgreSQL (Npgsql); schema maintained by idempotent startup initializers (no EF migrations)
- JWT outside Development, with server-side sessions
- xUnit v3; PostgreSQL integration tests on an isolated container

## Repository

```text
src/
├── Alpha.Api/              HTTP host and composition root (auth, sessions, middleware, error contract, payment adapters)
├── Alpha.Domain/           shared domain model
├── Alpha.Infrastructure/   AlphaDbContext, EF mappings, schema initializers, advisory lock
└── Modules/
    ├── IdentityAccess/     + .Contracts
    ├── Organizations/
    ├── Workforce/
    ├── Billing/            + .Contracts
    ├── Reporting/          + .Contracts
    ├── ReferenceData/
    └── Auditing/
tests/
├── Alpha.Api.Tests/           behavior, HTTP, security and PostgreSQL integration tests
├── Alpha.Architecture.Tests/  dependency, ownership and production-baseline gates
└── Alpha.Domain.Tests/        domain invariants
docs/                             architecture, security, database, reporting, development, release, specifications
tools/Alpha.SchemaVerifier/       read-only production schema verification
tools/production-readonly/        read-only production data checks (run only after approval)
integrations/google-apps-script/  mail gateway: login codes and invitation links (deployed by hand)
```

## Quick start

```powershell
docker compose up -d postgres
dotnet user-secrets --project src/Alpha.Api set "Security:DataProtectionKey" "<32-byte base64 key>"
dotnet run --project src/Alpha.Api       # Swagger: http://localhost:5080/swagger
```

An empty development database is bootstrapped automatically on first start. Details, configuration and Development authentication: [docs/development.md](docs/development.md).

## Verify

```powershell
docker compose -f docker-compose.test.yml up -d --wait
$env:ALPHA_TEST_POSTGRES = "Host=127.0.0.1;Port=55432;Database=alpha_tests;Username=alpha_test;Password=alpha_test_only"
dotnet restore AlphaBackend.slnx
dotnet build AlphaBackend.slnx --no-restore --configuration Release
dotnet test --solution AlphaBackend.slnx --no-build --configuration Release
```

Without `ALPHA_TEST_POSTGRES` the PostgreSQL tests are reported as skipped; that is not a full verification. The test database guard accepts only a loopback host and the database `alpha_tests`.

## Documentation

| Document | Read it for |
|---|---|
| [docs/architecture.md](docs/architecture.md) | Modules, dependency rules, data ownership, request flow, transactions, adding a feature |
| [docs/CODEMAP.md](docs/CODEMAP.md) | Where each feature and file lives |
| [docs/security.md](docs/security.md) | Threat model, authentication, authorization, tenant isolation, sensitive data, findings and open risks |
| [docs/reporting.md](docs/reporting.md) | Employer Interface 006 business rules: feedback resolution, code 18, documents, manufacturer routing, TEST simulation |
| [docs/database.md](docs/database.md) | Table inventory and ownership, schema initialization, change policy |
| [docs/development.md](docs/development.md) | Local setup, configuration, testing, troubleshooting |
| [docs/release.md](docs/release.md) | Production compatibility, release gates, rollback |
| [docs/SERVICE_PROVIDERS.md](docs/SERVICE_PROVIDERS.md) | External providers and their status |
| [docs/specifications/employer-interface/006/README.md](docs/specifications/employer-interface/006/README.md) | Employer Interface 006 sources and rules |

## Ground rules

- A push or merge to `main` deploys to production (Render). Follow [docs/release.md](docs/release.md).
- Never run tests, DDL or tools against production, staging or shared databases.
- Never commit secrets; `appsettings.json` holds placeholders only.
- Never log national IDs, bank data, report payloads, credentials or tokens.

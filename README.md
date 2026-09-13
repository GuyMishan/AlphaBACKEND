# Alpha Backend

Backend foundation for a multi-tenant pension operations platform.

## Architecture

- .NET 10 modular monolith
- ASP.NET Core REST API with OpenAPI
- PostgreSQL through Entity Framework Core
- Platform -> Organization -> Employer authorization hierarchy
- Immutable audit events for material changes
- Background worker boundary for imports, reports, payments, and feedback

The current milestone intentionally implements the platform, organization, employer, employee,
membership, and audit foundations. Pension reporting/payment integrations are represented as
module boundaries and are not connected to real providers yet.

## Run locally

1. Install the .NET 10 SDK and Docker.
2. Run `docker compose up -d postgres`.
3. Run `dotnet tool restore` and `dotnet restore`.
4. Create the first migration with:

   `dotnet ef migrations add InitialCreate --project src/Alpha.Infrastructure --startup-project src/Alpha.Api --output-dir Persistence/Migrations`

5. Apply it with:

   `dotnet ef database update --project src/Alpha.Infrastructure --startup-project src/Alpha.Api`

6. Run `dotnet run --project src/Alpha.Api`.

Swagger is available at `http://localhost:5080/swagger` in Development.

### Development authentication

Development uses a deliberately isolated header authentication scheme:

- `X-User-Id`: a UUID identifying the current user
- `X-Platform-Admin: true`: grants platform administration for local development only

This scheme is not registered outside Development. Production is configured for an external
OIDC/JWT authority through `Authentication:Authority` and `Authentication:Audience`.

## Verify

Run `dotnet build AlphaBackend.slnx` and `dotnet test AlphaBackend.slnx`.

## Important domain rules

- A user receives access through an active organization membership.
- Membership roles and platform roles are separate.
- Employer access can cover all employers or selected employers only.
- Every organization-owned row carries `OrganizationId`.
- Approved pension reports will be immutable snapshots in the reporting milestone.
- The application will orchestrate payments; it will not custody employer funds.

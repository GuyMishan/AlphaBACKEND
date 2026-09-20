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

### One-time login codes

Login requests `POST /api/auth/otp/request` with `{ "nationalId": "...", "phone": "...", "channel": "sms" }` (or `"email"`), then verifies `{ "challengeId": "...", "code": "123456" }` at `POST /api/auth/otp/verify`. Only verification issues a JWT. Codes expire after five minutes, accept at most five attempts, can be used once, and may be resent after 60 seconds. The prototype login endpoint has been removed.

Configure a JWT signing key of at least 32 characters in `PrototypeAuth__SigningKey` and real delivery providers before using login:

- SMS: `Otp__Sms__Url` (provider HTTPS endpoint), `Otp__Sms__Token` (Bearer token). The server posts JSON `{ "to": "05...", "message": "..." }`; use an adapter if your SMS provider has a different contract.
- Email: `Otp__Email__Host`, `Otp__Email__Port` (default 587), `Otp__Email__From`, `Otp__Email__Username`, `Otp__Email__Password`, and optionally `Otp__Email__EnableSsl` (default true).
- To retain the prototype platform administrator, explicitly set `PrototypeAuth__NationalId`, `PrototypeAuth__Phone`, and `PrototypeAuth__AdminEmail` to real, controlled destinations. Otherwise that account cannot log in. Existing ordinary users need a registered phone and email for both delivery choices.

Delivery failures return 503 and invalidate the challenge. Keep the signing key and delivery credentials in environment secrets. The SMS provider contract must be adapted to the provider selected for production.

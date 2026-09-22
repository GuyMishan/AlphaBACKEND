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

Login requests `POST /api/auth/otp/request` with `{ "nationalId": "...", "phone": "...", "channel": "email" }`, then verifies `{ "challengeId": "...", "code": "123456" }` at `POST /api/auth/otp/verify`. Only verification issues a JWT. Codes expire after five minutes, accept at most five attempts, can be used once, and may be resent after 60 seconds. The prototype login endpoint has been removed.

Configure a JWT signing key of at least 32 characters in `PrototypeAuth__SigningKey` and real delivery providers before using login:

- SMS is disabled by default. To enable later, set `Otp__Sms__Enabled=true`, plus `Otp__Sms__Url` (provider HTTPS endpoint), `Otp__Sms__Token` (Bearer token). The server posts JSON `{ "to": "05...", "message": "..." }`; use an adapter if your SMS provider has a different contract.
- Email using a personal Gmail account with no domain: deploy `integrations/google-apps-script/Code.gs` under the Gmail account, configure the shared secret as described below, and set `Otp__Email__AppsScript__Url` and `Otp__Email__AppsScript__Secret` in Render. This HTTPS route takes precedence over other configured email transports and works on Render Free. Google currently limits consumer Apps Script accounts to 100 email recipients per day.
- Optional alternative with a verified domain: set `Otp__Email__ResendApiKey` and `Otp__Email__From`. SMTP is also supported via `Otp__Email__Host`, `Otp__Email__Port` (default 587), `Otp__Email__From`, `Otp__Email__Username`, `Otp__Email__Password`, and `Otp__Email__EnableSsl` (default true); Render Free blocks standard SMTP ports.
- To retain the prototype platform administrator, explicitly set `PrototypeAuth__NationalId`, `PrototypeAuth__Phone`, and `PrototypeAuth__AdminEmail` to real, controlled destinations. Otherwise that account cannot log in. Existing ordinary users need a registered phone for lookup and a registered email for delivery.

The email-only login UI has no SMS option. The API rejects SMS requests and verification while `Otp__Sms__Enabled` is false, regardless of clients sending their own HTTP requests.

Delivery failures return 503 and invalidate the challenge. Keep the signing key and delivery credentials in environment secrets. The SMS provider contract must be adapted to the provider selected for production.

#### Personal Gmail setup (Google Apps Script)

1. Sign in to [script.google.com](https://script.google.com/) with the Gmail account that will send login codes. Create a new project and replace `Code.gs` with the contents of [`integrations/google-apps-script/Code.gs`](integrations/google-apps-script/Code.gs).
2. Generate a unique random secret of at least 32 characters using a password manager. In **Project Settings → Script Properties**, add `ALPHA_OTP_SECRET` with that value. Do not put the secret in source control or chat.
3. **Deploy → New deployment → Web app**. Choose **Execute as: Me** and **Who has access: Anyone**. Authorize sending email with this account, deploy, and copy the `/exec` URL. Only requests presenting the secret may trigger mail. Do not share the secret; the web app URL alone cannot send a code.
4. In Render **AlphaBACKEND → Environment**, add `Otp__Email__AppsScript__Url` (the `/exec` URL) and `Otp__Email__AppsScript__Secret` (the same secret as step 2). Save the values without triggering a deploy until the backend code is ready. Do not expose either value in the frontend.
5. For the prototype platform administrator, set `PrototypeAuth__AdminEmail` to the account that should receive the code and set `PrototypeAuth__NationalId`/`PrototypeAuth__Phone` to the matching login identity (the existing prototype credentials are supported once an admin email is configured). Verify a real email delivery and successful login before deploying the new frontend.

If you edit the Apps Script code, deploy a **new version** of the web app. On a personal Gmail account, this uses your own mailbox and its daily send quota. The backend stores hashed, expiring codes; the script only delivers them.


## Automatic ALPHA billing

ALPHA billing is provider-agnostic. Plans, usage, billing periods, payments, retries and suspension are handled by the ALPHA billing domain; the selected payment provider only executes payment operations.

Automatic monthly billing is disabled by default. Configure it with environment variables (double underscore notation on hosted environments):

- `Billing__AutomaticBillingEnabled=true` enables the hosted billing cycle.
- `Billing__JobIntervalMinutes=60` controls how often the worker checks for work.
- `Billing__CatchUpMonths=3` makes the worker also create missing completed monthly periods after downtime.
- `Billing__RetryDelayHours=24` controls the delay between failed-payment retries.
- `Billing__MaxPaymentAttempts=4` caps automatic attempts for one payment.
- `Billing__GracePeriodDays=7` suspends an unpaid billing account after the grace period.

The worker only bills completed calendar months. Existing billing-period, payment and payment-attempt unique indexes provide idempotency/concurrency guards. Historical periods use only pricing components whose effective dates cover that period; current pricing is never retroactively substituted.

# Production compatibility and release procedure

Production is live: the API runs on Render (built from the `Dockerfile`), the database and storage are on Supabase ([SERVICE_PROVIDERS.md](SERVICE_PROVIDERS.md)). Render deploys `main` automatically, so **a push or merge to `main` is a production deployment**. CI (`.github/workflows/ci.yml`) builds and tests; it does not deploy.

## What every release must preserve

- HTTP methods, paths, request/response contracts and status codes (245 route declarations, pinned).
- Authentication and session behavior for legitimate users; tenant isolation; authorization semantics.
- The single `AlphaDbContext`, the 67 tables, the EF model and the 31 initializers in order (all pinned).
- Encryption formats and purpose strings (`alpha:v1:`, `ALP\x01`) and the data-protection key.
- Append-only audit, immutable or superseded reporting/financial evidence, idempotency keys, advisory locks, Employer Transfer atomicity.

A deliberate change to any of these needs its own approval, a compatibility note and updated baselines in the same change.

## Behavior changes in this release

| Change | Who notices | Compatibility |
|---|---|---|
| `GET/POST /api/platform/users` no longer return `externalSubject`, `nationalIdEncrypted`, `nationalIdLookupHash`, `phoneEncrypted`, `phoneLookupHash` | Platform-admin UI | Confirm the frontend does not read the removed fields |
| A session whose token carries a platform-admin claim is revoked if the user is no longer a platform admin | Demoted admins | Must sign in again |
| Anonymous requests to unknown routes return 401 instead of 404 (fallback authorization) | Nobody legitimate | — |
| Every error response is RFC 9457 Problem Details (`application/problem+json`) with `errorCode`, `traceId` and, for validation, `errors`; empty-bodied 401/403/404/405/415/429 now have a body; unique violations and deadlocks are 409 instead of 500; upstream failures 502/504 | Frontend error handling | The deprecated `error` member keeps the previous value. Two breaking details: the manual-report validation `errors` is an object (`errors.request`), and an error member `status` is now `resourceStatus`. Full table: [architecture.md](architecture.md#migration-from-the-previous-error-formats) |
| OTP request during the 60-second resend cooldown, or past 5 codes per hour, returns `200` (no code sent) instead of `429` | Users who press "resend" quickly | Same body as a normal request; the frontend's resend timer still applies |
| Invitation links use `Frontend:BaseUrl` (fallback: the production frontend), never the request `Origin`/`Host` | Admins inviting from a non-production frontend | Set `Frontend:BaseUrl` in every environment, including local development |
| Payment-method setup builds the provider callback URL only from `Payments:PublicApiBaseUrl` (absolute https, no credentials/query/fragment); missing or invalid → `503 PAYMENT_CALLBACK_NOT_CONFIGURED` before any provider call, never the request `Host` | Billing admins, if the setting is absent | Set it in Render before the release (blocker 6) |
| Mail gateway script (`integrations/google-apps-script/Code.gs`) accepts invitation messages and bodies up to 4 KB | Invitations by e-mail | **Redeploy the Apps Script** ([procedure](#mail-gateway-redeployment-apps-script)); until then invitation e-mails fail with 503 when the gateway is configured |
| Clearinghouse delivery is selected explicitly by `EmployerInterface006:ClearinghouseProvider` (`Simulated` by default); startup stops on an invalid or contradictory selection instead of silently using the mock or refusing transmissions | Operators | Same simulator as today ([reporting.md](reporting.md#clearinghouse-provider-simulated-or-real)); remove `SimulatedVault__Enabled` from Render |
| Startup fails with a clear message if `Security:DataProtectionKey` or the token signing configuration is missing/invalid | Operators | A working deployment already has both |
| Legacy feedback normalization runs once and records `legacy-feedback-normalization-v1`; a malformed row no longer fails startup | Operators | First start after deploy does the same work as today |
| Membership changes, pension-mix and report-employee saves are atomic; access changes take the organization lock | Concurrent last-admin removal gets one 409 | — |
| Startup encryption backfill is bounded, background, coordinated, never stops the host, and stops running after a clean pass | Operators (log lines) | Same encryption formats |
| `IdentitySchemaInitializer` no longer contains the 2026-09-21 "set all user e-mails" statement | Nobody (already applied in production) | Verify the marker read-only (below) |
| `ReverseProxy:*` settings added, off by default | Operators | Enable only after the topology check |
| Only the anonymous credential endpoints (`/api/auth/otp/*`, `/api/auth/register/*`) use the 20/min per-client bucket; `/api/auth/session` and `/api/auth/logout` use the per-user bucket. IPv6 clients are grouped by /64. A 429 carries `Retry-After` | Users | Session checks no longer exhaust the login limit |
| `FrontendProxy:SharedSecret` added, off by default: with it, the frontend server's `X-Alpha-Client-IP` is the client address for rate limits and audit | Operators, frontend | [Procedure](#client-ip-through-the-frontend); without it behavior is unchanged |
| Field validation messages are under `errors.<field>` instead of `errors.request` at the listed sites; free-text business errors in IdentityAccess, Organizations and Workforce have specific codes, same `detail` | Frontend forms | [Table](architecture.md#migration-from-the-previous-error-formats); the deprecated `error` alias of those business errors is now the code |
| `Database:ApplyMigrations` removed; `Database:BootstrapEmptyDatabase` added (off outside Development) | Operators | Remove/leave unset in production |
| Dockerfile restores every module project and ships the V6 workbook | Deployment | Previous Dockerfile could not build the modular solution |
| EF SQL command logging lowered to Warning in `appsettings.json` | Operators reading logs | Errors still logged; fewer SQL statements in production logs |

No database schema change relative to production `main` and no migration (EF model identical to `46202eb`; the one nullable column added on `main` after `90c7ba6` is already deployed by `main`, see [database.md](database.md#schema-verification)).

## Release blockers (go / no-go)

1. **`main` must be frozen and fully ported.** This tree contains `origin/main` up to `46202eb` (121 commits ported into the module layout up to `90c7ba6`, then 16 more merged on 2026-10-12: collective-agreement refund preflight, negative product lineage, local document/source-change feedback signals; same 245 routes, EF model identical to `46202eb`). Any later commit on `main` must be ported the same way before release; freeze `main` until the release merge.
2. **Build and tests:** Release build with 0 warnings; full suite with `ALPHA_TEST_POSTGRES` set: 0 failed, 0 skipped; `docker build .` succeeds and the container starts against a scratch database.
3. **Production read-only verification** ([procedure](#production-read-only-verification)), after explicit approval: no Blocking schema result, every Review item explained, data checks reviewed.
4. **Backup:** a restorable production backup exists and a restore into an isolated environment has been rehearsed.
5. **Frontend acceptance of the error contract** ([below](#error-contract-rollout)): the frontend repository was not available in A12.3, so this is open. The frontend owner confirms, against a staging or local backend, that login, session expiry, 403/404 pages, plan-limit and invitation conflicts, manual-report validation messages and rate limiting still display correctly.
6. **Render environment** matches [the table below](#render-environment).
7. **Mail gateway** redeployed and smoke-tested ([procedure](#mail-gateway-redeployment-apps-script)).

No-go on any failing or skipped test, unexplained schema difference, missing backup, or open item above.

## Render environment

Values the release depends on (double underscores replace `:`). Check them in the Render dashboard before deploying; the release changes none of the secrets.

| Variable | Required value |
|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` (set by the Dockerfile; must not be overridden: `Development` enables header authentication, Swagger and demo data) |
| `ConnectionStrings__AlphaDatabase` | Unchanged. Keyword form or a `postgres://` URL (the URL form always requires SSL). `DATABASE_URL` is not read |
| `Security__DataProtectionKey`, `PrototypeAuth__SigningKey`, `Otp__Email__AppsScript__Url`, `Otp__Email__AppsScript__Secret` | Unchanged; startup now stops if the data-protection key is not 32-byte Base64 or the signing key is under 32 bytes |
| `Frontend__BaseUrl` | Public frontend URL (invitation links, payment return pages) |
| `FrontendProxy__SharedSecret` | Optional: a random value of at least 32 bytes, identical to the frontend's `ALPHA_PROXY_SECRET` ([procedure](#client-ip-through-the-frontend)). Shorter values stop startup |
| `Payments__PublicApiBaseUrl` | Optional on Render: when unset, Render's own `RENDER_EXTERNAL_URL` is used. An invalid explicit value makes payment-method setup return 503 |
| `EmployerInterface006__ClearinghouseProvider` | `Simulated` (the intended mode now; also the `appsettings.json` default, but set it explicitly) |
| `EmployerInterface006__EnvironmentCode` | Unset or `1`; any other value stops startup while `Simulated` is selected |
| `EmployerInterface006__SimulatedVault__Enabled` | **Remove** (the former switch); `false` stops startup |
| `EmployerInterface006__SimulatedVault__RootDirectory` | Unset (`/app/simulated-clearinghouse-vault`, lost on restart) or a persistent-disk path ([reporting.md](reporting.md#clearinghouse-provider-simulated-or-real)) |
| Must be unset or `false` | `ASPNETCORE_FORWARDEDHEADERS_ENABLED`, `ReverseProxy__Enabled` (until the topology check), `EmployerInterface006__AllowMockTransmission`, `Reporting__ManufacturerRouting__EnableHybridDispatch`, `Payments__AllowFakeProvider`, `Billing__AutomaticBillingEnabled`, `Database__BootstrapEmptyDatabase` |
| Not used | CORS settings (the API configures no CORS, as in the current production) |

After the deploy, the log shows `Clearinghouse provider: Simulated (SimulatedVault); Employer Interface 006 environment code 1.` A configuration error stops the new instance before it serves traffic; read the `Invalid configuration: ...` line in the deploy log.

## Production read-only verification

Two independent checks, each with its own least-privilege login. Neither writes data or schema; both need explicit approval before they run, and the person running them must be authorized for production access. Connection strings are typed into the shell session only (never a file, the repository or a ticket) and removed afterwards.

| | Schema verification | Data checks |
|---|---|---|
| Tool | `tools/Alpha.SchemaVerifier` ([details](database.md#production-schema-verification)) | `tools/production-readonly/data-checks.sql` with `psql` |
| Reads | `pg_catalog` only | marker keys/timestamps, and `count(*)` over the encrypted columns of 8 tables |
| Returns | catalog metadata (names, types, constraints) | keys, timestamps and counts; never a stored value |
| Login | `alpha_schema_reader`: `CONNECT` only, no table privileges | `alpha_readonly_checks`: `CONNECT`, `USAGE` on 4 schemas, column-level `SELECT` on the filtered columns only |
| Guards | `default_transaction_read_only=on`, explicit `READ ONLY` transaction, refuses to run if the server does not confirm read-only | role default `default_transaction_read_only=on`, `READ ONLY` transaction, `SHOW transaction_read_only`, ends with `ROLLBACK`; `statement_timeout 60s`, `lock_timeout 2s` |
| Tested by | `SchemaVerifierTests` (incl. a `CONNECT`-only role that cannot read or write) | `ProductionReadOnlyChecksTests` (no write statements; same scope as the backfill recount; runs with only `grants.sql` against a seeded database before and after the backfill) |

**Cost and impact.** The schema capture reads catalog views only (well under a second). The data checks run 8 sequential scans in one `REPEATABLE READ` snapshot; they take `ACCESS SHARE` locks only, which block nothing except DDL, and abort after 60 s per statement. Run them outside business hours and outside a billing or transmission run. Supabase compute is billed per instance, not per query.

**Logins** (created by the database owner in the Supabase SQL editor; creating them is part of the approval; drop them afterwards):

```sql
-- Schema verification: catalog only.
CREATE ROLE alpha_schema_reader LOGIN PASSWORD '<generated, 32+ chars>' CONNECTION LIMIT 1 VALID UNTIL '<tomorrow>';
-- Data checks: then run tools/production-readonly/grants.sql (column-level SELECT on the counted columns only).
CREATE ROLE alpha_readonly_checks LOGIN PASSWORD '<generated, 32+ chars>' CONNECTION LIMIT 1 VALID UNTIL '<tomorrow>';
GRANT CONNECT ON DATABASE postgres TO alpha_schema_reader, alpha_readonly_checks;

-- Afterwards:
DROP OWNED BY alpha_readonly_checks; DROP ROLE alpha_readonly_checks;
DROP OWNED BY alpha_schema_reader;   DROP ROLE alpha_schema_reader;
```

`ProductionReadOnlyChecksTests` applies `grants.sql` to a local login and proves the checks run with it while any other column, any other table and any write is refused. Column-level `SELECT` still lets the login read the counted values themselves (which can include legacy plaintext); the protection is the one-day validity, one connection, a reviewed script that only aggregates, and dropping the logins afterwards. Not chosen: the application role (it has DDL and write rights) or a `SECURITY DEFINER` counting function (a schema change).

**Commands** (PowerShell 7, repository root, `psql` 15+; secrets are typed at a prompt, never on the command line). Use the host from Supabase → Connect: the direct host `db.<project-ref>.supabase.co` with user `<role>`, or, if the direct host is unreachable (IPv6 only), the session pooler `aws-0-<region>.pooler.supabase.com` port 5432 with user `<role>.<project-ref>`.

```powershell
# Schema verification (tool details: database.md#production-schema-verification):
docker compose -f docker-compose.test.yml up -d --wait
$env:ALPHA_SCHEMA_EXPECTED_SERVER = "Host=127.0.0.1;Port=55432;Database=alpha_tests;Username=alpha_test;Password=alpha_test_only"
dotnet run -c Release --project tools/Alpha.SchemaVerifier -- expected --server-env ALPHA_SCHEMA_EXPECTED_SERVER --out expected.schema.json
$env:ALPHA_SCHEMA_TARGET = "Host=<host>;Port=5432;Database=postgres;Username=<alpha_schema_reader user>;SSL Mode=Require;Password=" + (Read-Host -MaskInput "alpha_schema_reader password")
dotnet run -c Release --project tools/Alpha.SchemaVerifier -- capture --connection-env ALPHA_SCHEMA_TARGET --out production.schema.json
Remove-Item Env:ALPHA_SCHEMA_TARGET
dotnet run -c Release --project tools/Alpha.SchemaVerifier -- compare expected.schema.json production.schema.json   # exit 0/1/2

# Data checks (psql prompts for the password):
psql "host=<host> port=5432 dbname=postgres user=<alpha_readonly_checks user> sslmode=require" -v ON_ERROR_STOP=1 -X -f tools/production-readonly/data-checks.sql
```

The data checks print, in order: `transaction_read_only` (one row), the markers (`Key`, `AppliedAt`), then 8 rows of `scope | remaining` in this order: `employees.people`, `employers.employer_payment_accounts`, `reporting.manual_reports`, `reporting.manual_report_employees`, `reporting.manual_report_payments`, `reporting.report_transmissions`, `reporting.manual_report_attachments`, `reporting.employer_interface_feedback`, followed by `ROLLBACK`.

**Expected output and decision**

| Output | Expected | Stop (no-go) when |
|---|---|---|
| `transaction_read_only` | `on` | anything else: stop immediately |
| Markers | `set-all-existing-user-emails-gaiu01999-20260921` present; `sensitive-data-backfill-v1` and `legacy-feedback-normalization-v1` absent before this release (production does not record them yet) | the backfill marker is present before this release (unknown writer) |
| Plaintext counts (8 rows) | any non-negative number before the release; it sizes the first backfill run | a statement fails (missing table or column: schema drift, compare with the schema verification) or times out (plan a batched run instead) |
| Same counts after the release | all 0 and `sensitive-data-backfill-v1` present | non-zero with the marker present (a writer stores plaintext: defect) |
| Schema comparison | Info/Review only, as listed in [database.md](database.md#production-schema-verification) | any Blocking result |

### Sensitive-data backfill

If all counts are zero, the first start runs a quick verification pass (hash refresh only) and records `sensitive-data-backfill-v1`. If counts are non-zero, the backfill encrypts them in batches after startup; watch for `Sensitive-data backfill completed` or for per-row errors (table and Id only) that need manual repair. `Security:EnableSensitiveDataBackfill=false` disables it entirely; use that only after the counts above are zero.

### Client IP behind Render

Render does not document its proxy addresses or hop count. Establish them before enabling `ReverseProxy`:

1. Deploy with `ReverseProxy:Enabled=true`, `ReverseProxy:KnownProxies:0=127.0.0.1` (trusts nothing real, so behavior is unchanged) and `ReverseProxy:DiagnosticRequestCount=20`.
2. From a known network, send requests with and without a fake `X-Forwarded-For: 198.51.100.77`. Read the `Reverse-proxy diagnostic` log lines: the peer address range, and where your real IP and the fake value appear in the chain.
3. Configure `ReverseProxy:KnownNetworks` to the observed peer range only, and `ReverseProxy:ForwardLimit` to the number of trusted hops between your real IP and the end of the chain (1 when the peer appends your IP last; 2 when another trusted proxy such as Cloudflare sits in front, whose published ranges must then also be listed).
4. Set `DiagnosticRequestCount=0`, redeploy, and confirm: your real IP appears in a new audit row; the fake value never does.

Until this is done, leave `ReverseProxy:Enabled=false` (current behavior: per-proxy rate limits and audit IPs).

### Client IP through the frontend

Browser requests reach the API from the Next.js route handler (`src/app/api/backend/[...path]/route.ts`), so the API sees Vercel's shared egress addresses whatever `ReverseProxy` says. The frontend server attests the client address instead:

1. Generate a secret: `openssl rand -base64 48`. Store it as `FrontendProxy__SharedSecret` on Render and as `ALPHA_PROXY_SECRET` (server-only: never `NEXT_PUBLIC_`) on Vercel, Production and Preview environments that call this backend.
2. Frontend change in the proxy route, when building the outgoing `headers` (the route already builds them from scratch, so incoming `X-Alpha-*` headers are never copied):

   ```ts
   const proxySecret = process.env.ALPHA_PROXY_SECRET;
   // On Vercel, x-real-ip is set by the platform edge from the TCP connection and cannot be supplied by the browser.
   const clientIp = request.headers.get("x-real-ip")?.trim();
   if (proxySecret && clientIp) {
     headers.set("X-Alpha-Proxy-Secret", proxySecret);
     headers.set("X-Alpha-Client-IP", clientIp);
   }
   ```

   Do not use the leftmost `X-Forwarded-For` value, and do not send the headers when hosting outside Vercel unless that platform also overwrites `x-real-ip`. The API accepts the address only with the exact secret, exactly one header value and a valid IP; otherwise it falls back to the peer address.
3. Deploy the backend first (the headers are ignored until the secret is set), then set both variables and deploy the frontend. Confirm in a new audit row (`ip`) that your real address appears.
4. Rotation: set the new value on both sides and redeploy both. While the values differ, frontend traffic falls back to the shared Vercel buckets (degraded limits, no outage).

## Mail gateway redeployment (Apps Script)

The gateway is deployed by hand from `integrations/google-apps-script/Code.gs`. The new script also accepts everything the current production backend sends, so redeploy it **before** the backend release; it fixes invitation e-mails for the current backend too.

**Requirements** (from `OtpDelivery` and `Code.gs`):

- Web app deployment, *Execute as: Me* (the sending Gmail account), *Who has access: Anyone*: the backend calls it without Google credentials; the shared secret in the body is the authentication.
- Script property `ALPHA_OTP_SECRET` equals Render `Otp__Email__AppsScript__Secret` (at least 32 characters); Render `Otp__Email__AppsScript__Url` is `https://script.google.com/macros/s/<deployment id>/exec`. The backend follows Google's redirect, waits up to 25 s and requires `{"ok":true}`.
- Keep the **same deployment** so the URL does not change: *Deploy → Manage deployments → edit (pencil) → Version: New version → Deploy*. *New deployment* creates a new URL and leaves the old one live.
- Scopes are unchanged (`MailApp` only). If Google asks for authorization, it must list only "Send email as you".
- Quota: 100 recipients per day on a consumer Gmail account, 1,500 on Workspace. Each smoke step below sends one e-mail.

**Steps**

1. In the Apps Script editor, note the current version number of the deployment (rollback target) and keep a copy of the current code.
2. Replace `Code.gs` with the repository file and save. Do not change the script property.
3. Edit the existing deployment, choose *New version*, deploy, and confirm the `/exec` URL is unchanged.
4. Direct smoke test (PowerShell 7; the secret is typed at a prompt; mails go to your own address):

   ```powershell
   $url = "<the /exec URL>"; $to = "<your address>"; $secret = Read-Host -MaskInput "ALPHA_OTP_SECRET"
   function Send-Gateway($body) { Invoke-RestMethod -Method Post -Uri $url -ContentType "application/json" -Body ($body | ConvertTo-Json -Compress) }
   Send-Gateway @{ secret = $secret; to = $to; code = "000000" }                       # ok: True, login-code mail arrives
   $link = "https://<frontend host>/invite?token=" + ("ab" * 32)
   Send-Gateway @{ secret = $secret; to = $to; code = $link; type = "invitation"; subject = "Alpha gateway test"; text = "Gateway test: $link" }   # ok: True
   Send-Gateway @{ secret = "x$secret"; to = $to; code = "000000" }                     # ok: False, no mail
   Remove-Variable secret
   ```

5. End to end (after the backend release, or now against the current backend): request a login code for a test account and receive it; create an invitation to a test address, receive it with a link to `Frontend:BaseUrl`, then cancel that invitation.

**Rollback:** edit the deployment and select the previous version. Both backend versions work with either script version for login codes; only invitations need the new one.

## Deployment sequence

1. Freeze `main`; record the currently deployed commit (rollback target); confirm no billing cycle or transmission is running.
2. Complete the blockers above; take a fresh backup.
3. Build the release commit directly on `main` (this working tree already contains `main` up to `90c7ba6` as uncommitted changes on a branch that `main` fast-forwards from): `git switch -c release/a12` → `git reset --soft origin/main` (moves only the branch pointer; files untouched) → `git add -A` → review `git status` / `git diff --cached --stat origin/main` → commit. If `main` moved past `90c7ba6`, port those commits first. Run the full verification again, open a pull request and let CI pass.
4. Merge to `main` (triggers the Render deploy). Startup applies only additive, idempotent initializer DDL.
5. Smoke tests: `GET /health` and `/health/db` return 200; anonymous `GET /api/scope` returns 401; a real user signs in, lists employers, opens a report and a document; a platform admin opens platform users and security events.
6. Watch logs for 30 minutes: initializer errors, `Sensitive-data backfill ...`, 5xx rate, 401 spikes, webhook failures.
7. Perform the client-IP topology steps when convenient (independent of the release).

## Error contract rollout

The backend change is designed to be deployable before the frontend changes, and either side can be rolled back alone:

| Backend | Frontend | Result |
|---|---|---|
| previous (production today) | current | Today's behavior |
| **this release** | current | Works through the deprecated `error` member, which carries the previous value. Breaks only where the frontend (a) reads the manual-report validation `errors` as an array, (b) reads a resource `status` from an error body (now `resourceStatus`), or (c) requires the content type to be exactly `application/json` or the body of a 401/403/404 to be empty. Release blocker 5 confirms none of these applies, or the frontend is fixed first |
| this release | updated | Target state: the frontend reads `errorCode`, `detail`, `errors` and shows `traceId` for support |
| previous (after a backend rollback) | updated | Works only if the updated frontend falls back: `errorCode ?? error?.toUpperCase()`, `detail ?? error`, `errors` as array **or** object, and tolerates empty error bodies. This fallback is a requirement on the frontend change |

**Frontend checklist** (manual until the frontend repository is reviewed; nothing here has been verified against the frontend):

1. Every error has `Content-Type: application/problem+json`. Parse error bodies regardless of content type, and do not require an empty body on 401, 403, 404, 405, 415 or 429.
2. Branch on `errorCode` (upper case), falling back to `error` for a rolled-back backend. Codes the UI must handle: `AUTHENTICATION_REQUIRED`, `AUTH_SESSION_EXPIRED` (also header `X-Alpha-Session-Expired`), `ACCESS_DENIED`, `RESOURCE_NOT_FOUND`, `RATE_LIMIT_EXCEEDED`, `VALIDATION_FAILED`, `MALFORMED_REQUEST`, `CONCURRENCY_CONFLICT`, `DUPLICATE_RESOURCE`, `PLAN_LIMIT_REACHED`, `PAYMENT_CALLBACK_NOT_CONFIGURED`, plus each module code it already handles in lower case.
3. `errors` is an object of message arrays (`errors.request` for request-level messages), not an array. Affected: the manual-report input validation on `POST .../manual-reports/`, `PUT .../manual-reports/{reportId}/details`, `PUT .../manual-reports/{reportId}/employees/{reportEmployeeId}`, `PUT .../manual-reports/{reportId}/deposits/{reportProductId}` (and every other 400 with free text).
4. `status` inside an error body is now the HTTP status; the resource state moved to `resourceStatus`. Affected: `invitation_not_pending` (409) on `POST /api/auth/register/request`, `POST /api/auth/register/verify` and `POST /api/organizations/{org}/invitations/{id}/cancel`; the "only a validated report can be transmitted" 409 on `POST .../manual-reports/{reportId}/transmissions`.
5. Show `detail` to users and `traceId` in support or error screens.
6. `POST /api/auth/otp/request` and `POST /api/auth/register/request` no longer answer 429 inside the resend cooldown; they answer 200 and the UI's own 60-second timer applies.

The `error` member is removed in a later release, only after the updated frontend is in production and the backend rollback window for this release has passed. Removing it changes the route contract baseline and needs its own approval.

## Rollback and roll-forward

No schema or storage format changes, so rollback is redeploying the previous commit; it runs against the same database. For the error format, see [Error contract rollout](#error-contract-rollout). Rolling back also restores the OTP `429` during the resend cooldown and the header-based invitation and payment-callback URLs; the mail gateway script can stay updated (it still accepts the previous backend's messages). The previous version ignores `ClearinghouseProvider` and selects the simulator from `SimulatedVault:Enabled` (true in its `appsettings.json`) and `EnvironmentCode=1`, so it keeps using the simulator. Sessions revoked by the admin-claim check stay revoked. The backfill marker and any values it encrypted remain valid for the previous version (same formats). If an initializer fails at startup the new instance never serves; whether Render keeps the previous instance depends on its health-check configuration, otherwise redeploy the previous commit.

For future schema changes: deploy additive DDL first, backfill, validate, switch code, and remove old structures only in a later release. Destructive changes need a restore plan.

## Observability to add

Health checks cover process and database connectivity only. Recommended: alerts on 5xx rate, login failures, webhook failures, billing-cycle and transmission failures, and backfill errors; per-module latency; the correlation ID (`X-Correlation-ID`) in structured logs.

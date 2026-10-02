# ALPHA payment processing stress audit

Last reviewed: 2026-10-02

## Scope

This audit covers the ALPHA subscription billing flow, not Employer Interface 006 pension-fund transfers:

1. billing-account details
2. hosted card setup
3. provider callback verification
4. token/payment-method persistence
5. monthly billing-period calculation
6. charge attempts and retries
7. provider/network ambiguity
8. refunds
9. provider configuration and failure modes
10. duplicate/concurrent delivery

Providers currently implemented: CardCom API 11 and PayPlus.

## Financial safety invariants

- A browser redirect is never proof that a card setup succeeded.
- A payment method becomes active only after a provider callback is verified server-to-server.
- A callback from an older setup attempt must never replace the latest payment method.
- A duplicate callback must not create a second payment method or roll state backwards.
- A monthly billing period has one deterministic payment identity.
- An explicit provider decline may be retried according to billing policy.
- A network timeout, request cancellation, process crash or unknown provider outcome is not treated as a decline.
- An ambiguous charge moves to ReconciliationRequired and is not automatically or manually re-charged.
- A stale Charging/Processing state is converted to ReconciliationRequired by the billing recovery pass.
- A pending/ambiguous refund continues to reserve its amount so another refund cannot exceed the remaining refundable balance.
- Refund idempotency keys cannot be reused for a different payment or amount.
- Direct one-off provider charges are disabled because they bypass the billing ledger.
- Fake payment processing is disabled unless explicitly enabled.
- Raw provider callback bodies and sensitive card data are never persisted.
- Provider customer/token/mandate identifiers never leave the backend API; the browser receives booleans plus masked card metadata only.
- Payment-method type cannot be changed while an active/default method exists; the existing method must be cancelled first.
- Suspended/cancelled billing accounts are never reactivated by a zero-value period or manual charge run.
- Partially refunded/refunded payments remain settled and can never be charged again for the same billing period.
- A provider “success” response without a transaction/refund identifier is treated as ambiguous and requires reconciliation.
- Local cancellation is authoritative before provider cleanup, so a provider outage cannot leave ALPHA charging enabled.
- Test/staging payment credentials and endpoints are never implicit production defaults.

## Scenarios

| Area | Scenario | Expected result |
| --- | --- | --- |
| Card setup | Normal hosted setup + verified callback | New payment method becomes Active and default |
| Card setup | Browser returns success before callback | UI waits; backend sync reports setup pending |
| Card setup | Browser reports success but no verified callback | Payment method does not become Active |
| Card setup | Old callback arrives after a newer setup started | Old callback is Ignored |
| Card setup | Replacement setup while old card is active | Old active method remains usable until new callback is verified |
| Card setup | Provider customer from callback mismatches pending customer | Callback rejected |
| Card setup | Callback says payment method inactive | Callback rejected |
| Callback | Exact duplicate after successful processing | Idempotent 200 duplicate |
| Callback | Concurrent duplicate while first handler is processing | Non-2xx so provider retries after winner completes |
| Callback | Previous transiently failed callback is delivered again | Callback is reprocessed |
| Callback | Handler crashed leaving Received | Stale event can be retried; fresh concurrent event is not acknowledged prematurely |
| Callback | Body exceeds 64 KiB with Content-Length | 413 |
| Callback | Chunked body exceeds 64 KiB | Stream is stopped and 413 returned without unbounded ReadToEnd |
| CardCom | Charge succeeds normally | Period/Payment/Attempt become Charged/Succeeded/Succeeded |
| CardCom | Same deterministic charge is retried | ExternalUniqTranId replay returns original result rather than creating a second charge |
| CardCom | Token missing | Non-retryable local token error; method becomes Failed |
| CardCom | Token expiry invalid/expired | Non-retryable local token error; method becomes Failed |
| CardCom | Provider returns explicit decline | Payment is Failed and period PastDue |
| PayPlus | Charge succeeds normally | Transaction UID stored and period charged |
| PayPlus | Billing correlation metadata | Deterministic billing key is sent in searchable more_info |
| PayPlus | Callback HMAC invalid | Callback rejected before IPN lookup |
| PayPlus | Callback HMAC valid | Server-to-server IPN lookup required before activation |
| PayPlus | IPN transaction status rejected/failed | Token cannot be activated |
| PayPlus | Provider returns malformed JSON | Normalized provider exception; no raw JSON parser crash |
| Billing | Same charged period is run twice | Provider called once |
| Billing | Explicit decline then retry succeeds | Same Payment retained, new PaymentAttempt created |
| Billing | Provider connection drops after dispatch | Period/Payment/Attempt become ReconciliationRequired |
| Billing | Manual RunPeriod called again while reconciliation required | Provider is not called again |
| Billing | Worker dies after persisting Charging/Processing | Recovery marks stale records ReconciliationRequired |
| Billing | Active payment method missing/cancelled | PastDue without provider call |
| Billing | Zero-value period on active account | Period closes without provider call |
| Billing | Zero-value period on suspended/cancelled account | Account state is preserved; no provider call |
| Billing | Refunded/partially-refunded payment period is run again | Existing settled payment is returned; provider is not called |
| Billing | Default payment method type differs from account type | Provider is not called |
| Billing | Manual run against suspended/cancelled account | Rejected without provider call |
| Billing | Concurrent period creation | Unique DB boundary prevents duplicate period/payment |
| Retry | PastDue explicit decline is within retry policy | Retry creates another attempt |
| Retry | ReconciliationRequired payment | Never included in automatic retry |
| Retry | Invalid token/expiry | Method marked Failed; no repeated provider attempts |
| Refund | Valid partial refund | Refund Succeeded and payment PartiallyRefunded |
| Refund | Full remaining refund | Payment Refunded |
| Refund | Refund exceeds remaining amount | Request rejected before provider call |
| Refund | Two concurrent refunds exceed remaining total | Serializable reservation prevents over-refund |
| Refund | Same idempotency key, same payment and amount | Existing refund returned |
| Refund | Same idempotency key reused for different request | Conflict |
| Refund | Provider explicit refund failure | Refund Failed; amount can be retried with a new key |
| Refund | Network timeout/exception after refund dispatch | Refund remains Pending and amount stays reserved |
| PayPlus refund | Refund original transaction | Uses /Transactions/RefundByTransactionUID |
| Configuration | Payment provider missing | Fails closed |
| Configuration | Fake provider without opt-in | Rejected |
| Configuration | Fake provider with explicit opt-in | Allowed for controlled development/test use |
| Provider outage | Setup/sync network failure | Normalized 503 payment_provider_unavailable |
| Provider outage | Cancel cleanup fails externally | ALPHA remains locally cancelled and cannot charge |
| Provider response | Charge approved but transaction id missing | ReconciliationRequired; never blindly retried |
| Provider response | Refund approved but refund id missing | Refund remains Pending; amount stays reserved |
| API exposure | Billing-account GET | Provider customer/token/mandate identifiers remain server-side |
| Configuration | PayPlus Invoice+ disabled | Charge does not request initial invoice |
| Configuration | PayPlus environment URL missing | Fails closed instead of using staging |
| Configuration | CardCom test merchant defaults | No test terminal/interface defaults are shipped |
| Unsafe API | Direct one-off platform provider charge | Rejected; callers must use billing-period run |

## Regression coverage

Automated tests are in:

- tests/Alpha.Api.Tests/CardComPaymentProviderTests.cs
- tests/Alpha.Api.Tests/PayPlusPaymentProviderTests.cs
- tests/Alpha.Api.Tests/PaymentProviderResolverTests.cs
- tests/Alpha.Api.Tests/BillingHardeningTests.cs
- tests/Alpha.Api.Tests/BillingPaymentFlowIntegrationTests.cs
- tests/Alpha.Api.Tests/BillingCyclePlannerTests.cs
- tests/Alpha.Api.Tests/BillingCalculatorTests.cs

PostgreSQL integration scenarios use ALPHA_TEST_POSTGRES and isolated temporary databases.

## Operational items that remain intentionally configuration-dependent

- Billing:AutomaticBillingEnabled is false in the repository default. Production must enable it deliberately after provider credentials and operational reconciliation procedures are ready.
- Real CardCom/PayPlus credentials and merchant capabilities cannot be exercised in CI. CI uses deterministic fake HTTP responses and PostgreSQL integration tests; a controlled provider sandbox/merchant test should still be run before turning on automatic production billing.
- Any ReconciliationRequired charge/refund must be checked against the provider back office/API before an operator resolves it. The system intentionally prefers “stop and reconcile” over risking a duplicate financial operation.

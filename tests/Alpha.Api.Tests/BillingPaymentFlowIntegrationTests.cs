using Alpha.Application.Billing;
using Alpha.Domain.Billing;
using Alpha.Domain.Organizations;
using Alpha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Alpha.Api.Tests;

public sealed class BillingPaymentFlowIntegrationTests
{
    [Fact]
    public async Task Successful_period_is_charged_once_even_when_run_twice()
    {
        await InIsolatedDatabase(async (db, ct) =>
        {
            var fixture = await CreateFixtureAsync(db, ct);
            var provider = new ScenarioProvider(_ =>
                Task.FromResult(new PaymentChargeResult(true, "tx-1", "invoice-1", null, null)));
            var service = Service(db, provider);

            var first = await service.RunPeriodAsync(
                fixture.Account.Id, fixture.Start, fixture.End, true, ct);
            var second = await service.RunPeriodAsync(
                fixture.Account.Id, fixture.Start, fixture.End, true, ct);

            Assert.Equal(BillingPeriodStatus.Charged, first.Status);
            Assert.Equal(BillingPeriodStatus.Charged, second.Status);
            Assert.Equal(1, provider.ChargeCalls);
            Assert.Single(await db.Payments.Where(x => x.BillingAccountId == fixture.Account.Id).ToListAsync(ct));
            Assert.Single(await db.PaymentAttempts.ToListAsync(ct));
        });
    }

    [Fact]
    public async Task Explicit_decline_can_retry_and_then_succeed_without_new_payment()
    {
        await InIsolatedDatabase(async (db, ct) =>
        {
            var fixture = await CreateFixtureAsync(db, ct);
            var results = new Queue<PaymentChargeResult>(
            [
                new(false, "", null, "declined", "Declined"),
                new(true, "tx-2", "invoice-2", null, null)
            ]);
            var provider = new ScenarioProvider(_ => Task.FromResult(results.Dequeue()));
            var service = Service(db, provider);

            var first = await service.RunPeriodAsync(
                fixture.Account.Id, fixture.Start, fixture.End, true, ct);
            var second = await service.RunPeriodAsync(
                fixture.Account.Id, fixture.Start, fixture.End, true, ct);

            Assert.Equal(BillingPeriodStatus.PastDue, first.Status);
            Assert.Equal(BillingPeriodStatus.Charged, second.Status);
            Assert.Equal(2, provider.ChargeCalls);
            Assert.Single(await db.Payments.Where(x => x.BillingAccountId == fixture.Account.Id).ToListAsync(ct));
            Assert.Equal(2, await db.PaymentAttempts.CountAsync(ct));
        });
    }

    [Fact]
    public async Task Provider_exception_requires_reconciliation_and_never_blindly_retries()
    {
        await InIsolatedDatabase(async (db, ct) =>
        {
            var fixture = await CreateFixtureAsync(db, ct);
            var provider = new ScenarioProvider(_ => throw new HttpRequestException("connection dropped"));
            var service = Service(db, provider);

            var first = await service.RunPeriodAsync(
                fixture.Account.Id, fixture.Start, fixture.End, true, ct);
            var second = await service.RunPeriodAsync(
                fixture.Account.Id, fixture.Start, fixture.End, true, ct);

            Assert.Equal(BillingPeriodStatus.ReconciliationRequired, first.Status);
            Assert.Equal(BillingPeriodStatus.ReconciliationRequired, second.Status);
            Assert.Equal(BillingPaymentStatus.ReconciliationRequired, first.PaymentStatus);
            Assert.Equal("payment_reconciliation_required", second.Error);
            Assert.Equal(1, provider.ChargeCalls);

            var payment = await db.Payments.SingleAsync(ct);
            var attempt = await db.PaymentAttempts.SingleAsync(ct);
            Assert.Equal(BillingPaymentStatus.ReconciliationRequired, payment.Status);
            Assert.Equal(BillingPaymentAttemptStatus.ReconciliationRequired, attempt.Status);
        });
    }

    [Fact]
    public async Task Stale_charging_state_is_moved_to_reconciliation_instead_of_retried()
    {
        await InIsolatedDatabase(async (db, ct) =>
        {
            var fixture = await CreateFixtureAsync(db, ct);
            var period = new BillingPeriod(fixture.Account.Id, fixture.Start, fixture.End);
            period.SaveCalculation(20m, 20m, "{}");
            period.MarkCharging();
            var payment = new Payment(fixture.Account.Id, period.Id, 20m, "ILS", "billing:stale");
            payment.MarkProcessing("Fake");
            var attempt = new PaymentAttempt(payment.Id, 1, "Fake", "billing:stale:attempt:1");
            db.AddRange(period, payment, attempt);
            await db.SaveChangesAsync(ct);

            var provider = new ScenarioProvider(_ =>
                Task.FromResult(new PaymentChargeResult(true, "should-not-run", null, null, null)));
            var service = Service(db, provider);

            await service.RetryPastDueAsync(
                TimeSpan.FromDays(7), TimeSpan.Zero, 4, ct);

            db.ChangeTracker.Clear();
            Assert.Equal(BillingPeriodStatus.ReconciliationRequired,
                (await db.BillingPeriods.SingleAsync(x => x.Id == period.Id, ct)).Status);
            Assert.Equal(BillingPaymentStatus.ReconciliationRequired,
                (await db.Payments.SingleAsync(x => x.Id == payment.Id, ct)).Status);
            Assert.Equal(BillingPaymentAttemptStatus.ReconciliationRequired,
                (await db.PaymentAttempts.SingleAsync(x => x.Id == attempt.Id, ct)).Status);
            Assert.Equal(0, provider.ChargeCalls);
        });
    }

    [Fact]
    public async Task Missing_active_payment_method_moves_period_to_past_due_without_provider_call()
    {
        await InIsolatedDatabase(async (db, ct) =>
        {
            var fixture = await CreateFixtureAsync(db, ct);
            fixture.Method.MarkStatus(BillingPaymentMethodStatus.Cancelled);
            await db.SaveChangesAsync(ct);
            var provider = new ScenarioProvider(_ =>
                Task.FromResult(new PaymentChargeResult(true, "should-not-run", null, null, null)));

            var result = await Service(db, provider).RunPeriodAsync(
                fixture.Account.Id, fixture.Start, fixture.End, true, ct);

            Assert.Equal(BillingPeriodStatus.PastDue, result.Status);
            Assert.Equal("payment_method_not_active", result.Error);
            Assert.Equal(0, provider.ChargeCalls);
        });
    }


    [Fact]
    public async Task Non_retryable_token_error_deactivates_payment_method()
    {
        await InIsolatedDatabase(async (db, ct) =>
        {
            var fixture = await CreateFixtureAsync(db, ct);
            var provider = new ScenarioProvider(_ =>
                Task.FromResult(new PaymentChargeResult(
                    false, "", null, "expiry_required", "Stored card expiry is invalid.")));
            var service = Service(db, provider);

            var first = await service.RunPeriodAsync(
                fixture.Account.Id, fixture.Start, fixture.End, true, ct);
            var second = await service.RunPeriodAsync(
                fixture.Account.Id, fixture.Start, fixture.End, true, ct);

            Assert.Equal(BillingPeriodStatus.PastDue, first.Status);
            Assert.Equal("payment_method_not_active", second.Error);
            Assert.Equal(1, provider.ChargeCalls);
            Assert.Equal(BillingPaymentMethodStatus.Failed, fixture.Account.PaymentMethodStatus);
            Assert.Equal(BillingPaymentMethodStatus.Failed, fixture.Method.Status);
        });
    }


    [Fact]
    public async Task Zero_value_period_does_not_reactivate_suspended_account()
    {
        await InIsolatedDatabase(async (db, ct) =>
        {
            var fixture = await CreateFixtureAsync(db, ct);
            fixture.Account.MarkStatus(BillingAccountStatus.Suspended);
            await db.SaveChangesAsync(ct);

            var provider = new ScenarioProvider(_ =>
                Task.FromResult(new PaymentChargeResult(true, "should-not-run", null, null, null)));
            var service = new BillingCycleService(
                db,
                new FixedUsageCollector(new BillingUsageSnapshot(0, 0, 0, 0, 0)),
                new BillingCalculator(),
                new ScenarioResolver(provider));

            var result = await service.RunPeriodAsync(
                fixture.Account.Id, fixture.Start, fixture.End, true, ct);

            Assert.Equal("billing_account_not_chargeable", result.Error);
            Assert.Equal(BillingAccountStatus.Suspended, fixture.Account.Status);
            Assert.Equal(0, provider.ChargeCalls);
        });
    }

    [Fact]
    public async Task Cancelled_account_cannot_be_charged_manually()
    {
        await InIsolatedDatabase(async (db, ct) =>
        {
            var fixture = await CreateFixtureAsync(db, ct);
            fixture.Account.MarkStatus(BillingAccountStatus.Cancelled);
            await db.SaveChangesAsync(ct);

            var provider = new ScenarioProvider(_ =>
                Task.FromResult(new PaymentChargeResult(true, "should-not-run", null, null, null)));

            var result = await Service(db, provider).RunPeriodAsync(
                fixture.Account.Id, fixture.Start, fixture.End, true, ct);

            Assert.Equal("billing_account_not_chargeable", result.Error);
            Assert.Equal(BillingAccountStatus.Cancelled, fixture.Account.Status);
            Assert.Equal(0, provider.ChargeCalls);
        });
    }


    [Fact]
    public async Task Billing_cycle_never_uses_payment_method_of_different_type()
    {
        await InIsolatedDatabase(async (db, ct) =>
        {
            var fixture = await CreateFixtureAsync(db, ct);
            fixture.Account.UpdateBillingDetails(
                "Billing Test", "515151515", "billing@example.test",
                "1 Test Street", BillingPaymentMethodType.BankDebit);
            await db.SaveChangesAsync(ct);

            var provider = new ScenarioProvider(_ =>
                Task.FromResult(new PaymentChargeResult(true, "should-not-run", null, null, null)));

            var result = await Service(db, provider).RunPeriodAsync(
                fixture.Account.Id, fixture.Start, fixture.End, true, ct);

            Assert.Equal("payment_method_not_active", result.Error);
            Assert.Equal(0, provider.ChargeCalls);
        });
    }

    [Fact]
    public async Task Provider_approved_without_transaction_id_requires_reconciliation_and_no_retry()
    {
        await InIsolatedDatabase(async (db, ct) =>
        {
            var fixture = await CreateFixtureAsync(db, ct);
            var provider = new ScenarioProvider(_ =>
                Task.FromResult(new PaymentChargeResult(
                    false, "", null, "transaction_id_missing", "Approved without transaction id")));
            var service = Service(db, provider);

            var first = await service.RunPeriodAsync(
                fixture.Account.Id, fixture.Start, fixture.End, true, ct);
            var second = await service.RunPeriodAsync(
                fixture.Account.Id, fixture.Start, fixture.End, true, ct);

            Assert.Equal(BillingPeriodStatus.ReconciliationRequired, first.Status);
            Assert.Equal(BillingPaymentStatus.ReconciliationRequired, first.PaymentStatus);
            Assert.Equal("payment_reconciliation_required", second.Error);
            Assert.Equal(1, provider.ChargeCalls);
        });
    }

    private static BillingCycleService Service(AlphaDbContext db, ScenarioProvider provider) =>
        new(
            db,
            new FixedUsageCollector(new BillingUsageSnapshot(1, 2, 0, 0, 0)),
            new BillingCalculator(),
            new ScenarioResolver(provider));

    private static async Task<Fixture> CreateFixtureAsync(AlphaDbContext db, CancellationToken ct)
    {
        var organization = new Organization("Billing Test", OrganizationType.PayrollOffice);
        var account = new BillingAccount(organization.Id, null);
        account.UpdateBillingDetails("Billing Test", "515151515", "billing@example.test",
            "1 Test Street", BillingPaymentMethodType.CreditCard);
        account.UpdateProviderMetadata(BillingPaymentMethodStatus.Active, "customer-1", "token-1",
            "Visa", "4242", 12, 2035, "");

        var method = new PaymentMethod(account.Id, "Fake", BillingPaymentMethodType.CreditCard);
        method.Activate("customer-1", "token-1", "Visa", "4242", 12, 2035, "");
        account.SetDefaultPaymentMethod(method.Id);

        var start = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddMonths(1);
        var component = new BillingAccountPricingComponent(
            account.Id, BillingMetricType.Employee, BillingPricingType.PerUnit,
            10m, 0m, null, null, true, 1, start.AddDays(-1));

        db.AddRange(organization, account, method, component);
        await db.SaveChangesAsync(ct);
        return new Fixture(account, method, start, end);
    }

    private sealed record Fixture(
        BillingAccount Account,
        PaymentMethod Method,
        DateTimeOffset Start,
        DateTimeOffset End);

    private sealed class FixedUsageCollector(BillingUsageSnapshot snapshot) : IBillingUsageCollector
    {
        public Task<BillingUsageSnapshot> CollectAsync(
            BillingAccount account, DateTimeOffset periodStart, DateTimeOffset periodEnd,
            CancellationToken ct = default) => Task.FromResult(snapshot);
    }

    private sealed class ScenarioResolver(ScenarioProvider provider) : IPaymentProviderResolver
    {
        public IPaymentProvider Resolve(string? providerName = null) => provider;
    }

    private sealed class ScenarioProvider(
        Func<PaymentChargeRequest, Task<PaymentChargeResult>> charge) : IPaymentProvider
    {
        public string Name => "Fake";
        public int ChargeCalls { get; private set; }

        public Task<PaymentProviderCustomerResult> CreateCustomer(
            PaymentProviderCustomerRequest request, CancellationToken ct = default) =>
            Task.FromResult(new PaymentProviderCustomerResult("customer"));

        public Task<PaymentMethodSetupResult> CreatePaymentMethod(
            PaymentMethodSetupRequest request, CancellationToken ct = default) =>
            Task.FromResult(new PaymentMethodSetupResult("setup", "https://example.test"));

        public async Task<PaymentChargeResult> Charge(
            PaymentChargeRequest request, CancellationToken ct = default)
        {
            ChargeCalls++;
            return await charge(request);
        }

        public Task<PaymentRefundResult> Refund(
            PaymentRefundRequest request, CancellationToken ct = default) =>
            Task.FromResult(new PaymentRefundResult(true, "refund", null, null));

        public Task<PaymentMethodStatusResult> GetPaymentMethodStatus(
            string customerId, string paymentMethodId, CancellationToken ct = default) =>
            Task.FromResult(new PaymentMethodStatusResult(
                true, paymentMethodId, "Visa", "4242", 12, 2035, null, customerId));

        public Task CancelPaymentMethod(
            string customerId, string paymentMethodId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<PaymentMethodStatusResult> ResolvePaymentMethodFromCallback(
            string rawBody, IReadOnlyDictionary<string, string> headers,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static async Task InIsolatedDatabase(Func<AlphaDbContext, CancellationToken, Task> test)
    {
        var baseConnection = Environment.GetEnvironmentVariable("ALPHA_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnection)) return;
        var ct = TestContext.Current.CancellationToken;
        var database = "alpha_billing_flow_" + Guid.NewGuid().ToString("N");
        var cs = new NpgsqlConnectionStringBuilder(baseConnection) { Database = database, Pooling = false };

        await using (var admin = new NpgsqlConnection(baseConnection))
        {
            await admin.OpenAsync(ct);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin);
            await create.ExecuteNonQueryAsync(ct);
        }

        try
        {
            var options = new DbContextOptionsBuilder<AlphaDbContext>()
                .UseNpgsql(cs.ConnectionString).Options;
            await using var db = new AlphaDbContext(options);
            await db.Database.EnsureCreatedAsync(ct);
            await test(db, ct);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(baseConnection);
            await admin.OpenAsync(CancellationToken.None);
            await using var drop = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}

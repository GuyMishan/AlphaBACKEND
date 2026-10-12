namespace Alpha.Api.Payments;

public static class PaymentProviderSetup
{
    /// <summary>
    /// Concrete provider adapters stay in the host; Billing depends only on
    /// <see cref="IPaymentProvider"/> and <see cref="IPaymentProviderResolver"/>.
    /// </summary>
    public static IServiceCollection AddPaymentProviders(this IServiceCollection services)
    {
        services.AddScoped<FakePaymentProvider>();
        services.AddScoped<CardComPaymentProvider>();
        services.AddScoped<PayPlusPaymentProvider>();
        services.AddScoped<PaymentProviderResolver>();
        services.AddScoped<IPaymentProviderResolver>(sp => sp.GetRequiredService<PaymentProviderResolver>());
        services.AddScoped<IPaymentProvider>(sp => sp.GetRequiredService<PaymentProviderResolver>());
        services.AddHttpClient("cardcom", client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient("payplus", client => client.Timeout = TimeSpan.FromSeconds(30));
        return services;
    }
}

using Alpha.Modules.Reporting.Application;
using Alpha.Modules.Reporting.Application.Usage;
using Alpha.Modules.Reporting.Application.Snapshots;
using Alpha.Modules.Reporting.Contracts;
using Alpha.Modules.Reporting.Features.Attachments;
using Alpha.Modules.Reporting.Features.Corrections;
using Alpha.Modules.Reporting.Features.EmployerDocuments;
using Alpha.Modules.Reporting.Features.EmployerInterface006;
using Alpha.Modules.Reporting.Features.Feedback;
using Alpha.Modules.Reporting.Features.Import;
using Alpha.Modules.Reporting.Features.ManualReports;
using Alpha.Modules.Reporting.Features.PaymentAccounts;
using Alpha.Modules.Reporting.Features.PaymentConfirmations;
using Alpha.Modules.Reporting.Features.PreviousReferences;
using Alpha.Modules.Reporting.Features.Profiles;
using Alpha.Modules.Reporting.Features.Transmissions;
using Alpha.Modules.Reporting.Features.PublicReferenceData;
using Alpha.Modules.Reporting.Features.Validation;
using Alpha.Modules.Reporting.Infrastructure.Evidence;
using Alpha.Modules.Reporting.Infrastructure.Security;
using Alpha.Modules.Reporting.Infrastructure.Simulation;
using Alpha.Modules.Reporting.Infrastructure.Transmissions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Alpha.Modules.Reporting;

public static class ReportingModule
{
    public static IServiceCollection AddReportingModule(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.Configure<EmployerInterface006Options>(configuration.GetSection(EmployerInterface006Options.SectionName));
        services.Configure<SimulatedClearinghouseVaultOptions>(configuration.GetSection(SimulatedClearinghouseVaultOptions.SectionName));

        services.AddScoped<ReportPaymentAccountService>();
        services.AddScoped<IEmployeeDraftSnapshotSynchronizer, EmployeeDraftSnapshotSynchronizer>();
        services.AddSingleton<EmployerInterfaceSchemaRegistry>();
        services.AddSingleton<EmployerInterfaceIncomingValidator>();
        services.AddScoped<EmployerInterface006ExportService>();
        services.AddScoped<EmployerInterfaceService>();
        services.AddScoped<LegacyFeedbackNormalization>();
        services.AddScoped<EmployerInterfaceFileSequenceService>();
        services.AddScoped<IReportingUsageFactsReader, ReportingUsageFactsReader>();
        services.AddScoped<PaymentEvidenceStorage>();
        services.AddScoped<IMalwareScanner, ConfiguredMalwareScanner>();
        services.AddHttpClient("malware-scanner", client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddHttpClient("payment-evidence-storage", client => client.Timeout = TimeSpan.FromSeconds(60));

        AddTransmissionProviders(services, configuration, environment);
        return services;
    }

    /// <summary>
    /// Registers exactly one delivery path, chosen explicitly by <c>EmployerInterface006:ClearinghouseProvider</c>
    /// (see <see cref="ClearinghouseProviderSelection"/>): the TEST simulator, the mock, or a provider that refuses
    /// delivery. An invalid selection stops startup; nothing falls back to another provider.
    /// </summary>
    private static void AddTransmissionProviders(
        IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var selected = ClearinghouseProviderSelection.Resolve(configuration, environment);
        services.AddSingleton(new SelectedClearinghouseProvider(selected));
        // The simulator's own gates (provider, responder, inbox worker) follow the selection, not a second switch.
        services.PostConfigure<SimulatedClearinghouseVaultOptions>(options =>
            options.Enabled = selected == ClearinghouseProvider.Simulated);

        if (selected != ClearinghouseProvider.Simulated)
        {
            if (selected == ClearinghouseProvider.Mock)
                services.AddScoped<IReportTransmissionProvider, MockReportTransmissionProvider>();
            else
                services.AddScoped<IReportTransmissionProvider, UnavailableReportTransmissionProvider>();
            return;
        }

        services.AddScoped<IReportTransmissionProvider, SimulatedVaultReportTransmissionProvider>();
        services.AddScoped<SimulatedClearinghouseFeedbackIngestor>();
        services.AddScoped<SimulatedClearinghouseTechnicalFeedbackHandler>();
        services.AddHostedService<SimulatedClearinghouseResponder>();
        services.AddHostedService<SimulatedClearinghouseVaultWorker>();

        // Opt-in TEST-only manufacturer vaults. Every configured manufacturer
        // receives a separate provider identity, outbox, inbox and responder.
        foreach (var manufacturerSection in configuration
                     .GetSection("EmployerInterface006:SimulatedVault:Manufacturers").GetChildren())
        {
            if (!manufacturerSection.GetValue<bool>("Enabled")) continue;
            var manufacturer = SimulatedManufacturerVaultReportTransmissionProvider.ValidateKey(manufacturerSection.Key);
            services.AddScoped<IReportTransmissionProvider>(sp =>
                new SimulatedManufacturerVaultReportTransmissionProvider(
                    sp.GetRequiredService<IOptions<SimulatedClearinghouseVaultOptions>>(),
                    sp.GetRequiredService<IOptions<EmployerInterface006Options>>(),
                    manufacturer, sp.GetRequiredService<IConfiguration>()));
            services.AddSingleton<IHostedService>(sp =>
                new SimulatedManufacturerVaultHostedService(
                    sp.GetRequiredService<IServiceScopeFactory>(),
                    sp.GetRequiredService<IOptions<SimulatedClearinghouseVaultOptions>>(),
                    sp.GetRequiredService<IOptions<EmployerInterface006Options>>(),
                    sp.GetRequiredService<ILoggerFactory>(), manufacturer,
                    sp.GetRequiredService<IConfiguration>()));
        }
    }

    /// <summary>
    /// Request-pipeline step: rejects malformed manual-report bodies with the established 400 contract.
    /// Must run after authorization and security audit.
    /// </summary>
    public static IApplicationBuilder UseReportingModule(this IApplicationBuilder app) => app.UseReportingInputValidation();

    /// <summary>Startup step run after schema initialization: one-time normalization of legacy stored feedback.</summary>
    public static async Task InitializeReportingAsync(this IServiceProvider scopedServices, CancellationToken ct)
    {
        // Make the delivery path observable in every environment: provider names and the 006 environment code only.
        var selected = scopedServices.GetRequiredService<SelectedClearinghouseProvider>().Provider;
        var providers = scopedServices.GetServices<IReportTransmissionProvider>().Select(provider => provider.Name);
        scopedServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ReportingModule).FullName!).LogInformation(
            "Clearinghouse provider: {ClearinghouseProvider} ({TransmissionProviders}); Employer Interface 006 environment code {EnvironmentCode}.",
            selected, string.Join(", ", providers),
            scopedServices.GetRequiredService<IOptions<EmployerInterface006Options>>().Value.EnvironmentCode);

        await scopedServices.GetRequiredService<LegacyFeedbackNormalization>().RunAsync(ct);
    }

    public static IEndpointRouteBuilder MapReportingModule(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapManualReportEndpoints();
        endpoints.MapReportAttachmentEndpoints();
        endpoints.MapEmployerDocumentEndpoints();
        endpoints.MapPaymentConfirmationEndpoints();
        endpoints.MapDerivedReportEndpoints();
        endpoints.MapReportValidationEndpoints();
        endpoints.MapEmployerInterfaceEndpoints();
        endpoints.MapEmployerInterfaceProfileEndpoints();
        endpoints.MapEmployerPaymentAccountEndpoints();
        endpoints.MapOrganizationPaymentAccountEndpoints();
        endpoints.MapEmployerInterfacePreviousReferenceEndpoints();
        endpoints.MapReportTransmissionEndpoints();
        endpoints.MapReportFeedbackEndpoints();
        endpoints.MapFeedbackTreatmentAdministrationEndpoints();
        return endpoints;
    }

    /// <summary>006-specific public catalogs, attached to the host-owned <c>/api/reference-data</c> group.</summary>
    public static RouteGroupBuilder MapReportingReferenceData(this RouteGroupBuilder group) =>
        group.MapReportingReferenceDataEndpoints();
}

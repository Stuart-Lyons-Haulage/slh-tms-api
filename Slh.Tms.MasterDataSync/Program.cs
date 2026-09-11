using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();
        services.AddOptions<SyncOptions>()
            .Bind(context.Configuration.GetSection("MasterDataSync"))
            .PostConfigure(options => options.ApplyDefaults());

        services.AddHttpClient<GraphSharePointClient>();
        services.AddHttpClient<TmsCacheInvalidationClient>();
        services.AddScoped<SqlMasterDataRepository>();
        services.AddScoped<DeadLetterQueue>();
        services.AddScoped<MasterDataSyncOrchestrator>();
        services.AddSingleton<SyncTelemetry>();
    })
    .Build();

await host.RunAsync();

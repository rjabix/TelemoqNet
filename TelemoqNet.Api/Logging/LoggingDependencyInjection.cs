namespace TelemoqNet.Api.Logging;

public static class LoggingDependencyInjection
{
    public static IServiceCollection AddLoggingServices(
        this IServiceCollection services,
        IHostEnvironment environment)
    {
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.AddDebug();
            builder.AddEventSourceLogger();
        });

        if (environment.IsDevelopment() ||
            environment.IsEnvironment("Local"))
        {
            services.AddSingleton<ISessionStore, LocalCsvSessionStore>();
        }
        else
        {
            services.AddApplicationInsightsTelemetryWorkerService();
            services.AddSingleton<ISessionStore, AzureBlobSessionStore>();
        }

        return services;
    }
}
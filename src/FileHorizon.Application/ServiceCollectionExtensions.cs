using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using FileHorizon.Application.Configuration;
using Microsoft.Extensions.Logging; // added

namespace FileHorizon.Application;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        // Core services
        services.AddSingleton<Core.IFileProcessingService, Core.FileProcessingService>();
        // Infrastructure defaults
        services.AddSingleton<Abstractions.IFileProcessor, Infrastructure.FileProcessing.LocalFileTransferProcessor>();
        services.AddSingleton<Abstractions.IFileEventValidator, Validation.BasicFileEventValidator>();

        // Register pollers (concrete) then selector facade
        services.AddSingleton<Infrastructure.Polling.SyntheticFilePoller>();
        services.AddSingleton<Infrastructure.Polling.LocalDirectoryPoller>();
        services.AddSingleton<Abstractions.IFilePoller, Infrastructure.Polling.PollerSelector>();

        // Conditional queue registration: attempt Redis if enabled, else fallback to in-memory.
        services.AddSingleton<Abstractions.IFileEventQueue>(sp =>
        {
            var opts = sp.GetService<IOptions<RedisOptions>>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var validator = sp.GetRequiredService<Abstractions.IFileEventValidator>();
            var logger = loggerFactory.CreateLogger("QueueRegistration");
            if (opts?.Value is { Enabled: true } ro)
            {
                try
                {
                    logger.LogInformation("Registering RedisFileEventQueue (stream {Stream}, group {Group})", ro.StreamName, ro.ConsumerGroup);
                    return new Infrastructure.Redis.RedisFileEventQueue(
                        loggerFactory.CreateLogger<Infrastructure.Redis.RedisFileEventQueue>(),
                        validator,
                        opts);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to initialize RedisFileEventQueue; falling back to InMemoryFileEventQueue");
                }
            }
            logger.LogInformation("Using InMemoryFileEventQueue (Redis disabled or failed)");
            return new Infrastructure.Queue.InMemoryFileEventQueue(
                loggerFactory.CreateLogger<Infrastructure.Queue.InMemoryFileEventQueue>(),
                validator);
        });

        return services;
    }
}
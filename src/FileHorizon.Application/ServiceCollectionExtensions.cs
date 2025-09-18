using Microsoft.Extensions.DependencyInjection;

namespace FileHorizon.Application;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        // Core services
        services.AddScoped<Core.IFileProcessingService, Core.FileProcessingService>();
        // Infrastructure defaults
        services.AddScoped<Abstractions.IFileProcessor, Infrastructure.FileProcessing.NoOpFileProcessor>();
        services.AddSingleton<Abstractions.IFileEventQueue, Infrastructure.Queue.InMemoryFileEventQueue>();
        // Pollers: register concrete implementations then selector facade
        services.AddScoped<Infrastructure.Polling.SyntheticFilePoller>();
        services.AddScoped<Infrastructure.Polling.LocalDirectoryPoller>();
        services.AddScoped<Abstractions.IFilePoller, Infrastructure.Polling.PollerSelector>();
        services.AddSingleton<Abstractions.IFileEventValidator, Validation.BasicFileEventValidator>();
        return services;
    }
}
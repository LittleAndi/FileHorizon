using Microsoft.Extensions.DependencyInjection;

namespace FileHorizon.Application;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        // Core services
        services.AddSingleton<Core.IFileProcessingService, Core.FileProcessingService>();
        // Infrastructure defaults
        services.AddSingleton<Abstractions.IFileProcessor, Infrastructure.FileProcessing.LocalFileTransferProcessor>();
        services.AddSingleton<Abstractions.IFileEventQueue, Infrastructure.Queue.InMemoryFileEventQueue>();
        // Pollers: register concrete implementations then selector facade
        services.AddSingleton<Infrastructure.Polling.SyntheticFilePoller>();
        services.AddSingleton<Infrastructure.Polling.LocalDirectoryPoller>();
        services.AddSingleton<Abstractions.IFilePoller, Infrastructure.Polling.PollerSelector>();
        services.AddSingleton<Abstractions.IFileEventValidator, Validation.BasicFileEventValidator>();
        return services;
    }
}
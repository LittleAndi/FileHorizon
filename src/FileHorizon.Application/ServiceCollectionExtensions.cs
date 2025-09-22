using FileHorizon.Application.Common.Telemetry;
using FileHorizon.Application.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace FileHorizon.Application;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        // Core services
        services.AddSingleton<Abstractions.IFileProcessingTelemetry, Infrastructure.Telemetry.FileProcessingTelemetry>();
        services.AddSingleton<Core.IFileProcessingService, Core.FileProcessingService>();
        // Infrastructure defaults
        services.AddSingleton<Abstractions.IFileProcessor, Infrastructure.FileProcessing.LocalFileTransferProcessor>();
        services.AddSingleton<Abstractions.IFileEventValidator, Validation.BasicFileEventValidator>();

        // Register only the real local directory poller
        services.AddSingleton<Infrastructure.Polling.LocalDirectoryPoller>();
        services.AddSingleton<Abstractions.IFilePoller>(sp => sp.GetRequiredService<Infrastructure.Polling.LocalDirectoryPoller>());

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

        // Options placeholders (bound in host layer)
        services.AddOptions<PipelineOptions>();
        services.AddOptions<PollingOptions>();
        services.AddOptions<PipelineFeaturesOptions>(); // retained only for EnableFileTransfer gating

        // Register concrete background service implementations as singletons (not hosted yet)
        services.AddSingleton<Infrastructure.Orchestration.FilePollingBackgroundService>();
        services.AddSingleton<Infrastructure.Orchestration.FileProcessingBackgroundService>();

        // Composite hosted service decides at runtime which underlying services to start.
        services.AddSingleton<IHostedService>(sp =>
        {
            var role = sp.GetRequiredService<IOptions<PipelineOptions>>().Value.Role;
            var polling = sp.GetRequiredService<Infrastructure.Orchestration.FilePollingBackgroundService>();
            var processing = sp.GetRequiredService<Infrastructure.Orchestration.FileProcessingBackgroundService>();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("PipelineRoleSelector");
            return role switch
            {
                PipelineRole.Poller => new CompositeHostedService(logger, polling),
                PipelineRole.Worker => new CompositeHostedService(logger, processing),
                PipelineRole.All => new CompositeHostedService(logger, polling, processing),
                _ => new CompositeHostedService(logger, polling, processing)
            };
        });

        return services;
    }

    private sealed class CompositeHostedService(ILogger logger, params IHostedService[] hostedServices) : IHostedService
    {
        private readonly IReadOnlyList<IHostedService> _services = hostedServices;
        private readonly ILogger _logger = logger;
        private Activity? _lifecycleActivity;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _lifecycleActivity = TelemetryInstrumentation.ActivitySource.StartActivity("pipeline.lifetime", ActivityKind.Internal);
            _lifecycleActivity?.SetTag("pipeline.service.count", _services.Count);
            foreach (var svc in _services)
            {
                _logger.LogInformation("Starting hosted service {Service}", svc.GetType().Name);
                _lifecycleActivity?.AddEvent(new ActivityEvent($"start:{svc.GetType().Name}"));
                await svc.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            _lifecycleActivity?.SetStatus(ActivityStatusCode.Ok);
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            foreach (var svc in _services.Reverse())
            {
                _logger.LogInformation("Stopping hosted service {Service}", svc.GetType().Name);
                _lifecycleActivity?.AddEvent(new ActivityEvent($"stop:{svc.GetType().Name}"));
                await svc.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            _lifecycleActivity?.Dispose();
        }
    }
}
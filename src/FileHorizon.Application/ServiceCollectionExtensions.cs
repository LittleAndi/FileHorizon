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
        services.AddSingleton<Infrastructure.FileProcessing.FileProcessingOrchestrator>();
        // Orchestrator is now the default and only processor
        services.AddSingleton<Abstractions.IFileProcessor>(sp =>
            sp.GetRequiredService<Infrastructure.FileProcessing.FileProcessingOrchestrator>());
        services.AddSingleton<Abstractions.IFileEventValidator, Validation.BasicFileEventValidator>();
        // Processing adapters (initial local-only)
        services.AddSingleton<Abstractions.IFileContentReader, Infrastructure.Processing.LocalFileContentReader>();
        services.AddSingleton<Abstractions.IFileContentReader, Infrastructure.Processing.SftpFileContentReader>();
        services.AddSingleton<Abstractions.IFileSink, Infrastructure.Processing.LocalFileSink>();
        services.AddSingleton<Abstractions.IFileRouter, Infrastructure.Processing.SimpleFileRouter>();

        // Remote client factories
        services.AddSingleton<Abstractions.ISftpClientFactory, Infrastructure.Remote.SshNetSftpClientFactory>();

        services.AddSingleton<Infrastructure.Polling.LocalDirectoryPoller>();
        services.AddSingleton<Abstractions.IFilePoller>(sp =>
        {
            var features = sp.GetRequiredService<IOptions<PipelineFeaturesOptions>>().Value;
            if (!features.EnableLocalPoller)
            {
                throw new InvalidOperationException("Local poller requested but EnableLocalPoller=false");
            }
            return sp.GetRequiredService<Infrastructure.Polling.LocalDirectoryPoller>();
        });
        services.AddSingleton<Infrastructure.Polling.FtpPoller>(sp =>
        {
            var features = sp.GetRequiredService<IOptions<PipelineFeaturesOptions>>().Value;
            if (!features.EnableFtpPoller) throw new InvalidOperationException("FtpPoller requested but feature flag disabled");
            return ActivatorUtilities.CreateInstance<Infrastructure.Polling.FtpPoller>(sp);
        });
        services.AddSingleton<Infrastructure.Polling.SftpPoller>(sp =>
        {
            var features = sp.GetRequiredService<IOptions<PipelineFeaturesOptions>>().Value;
            if (!features.EnableSftpPoller) throw new InvalidOperationException("SftpPoller requested but feature flag disabled");
            return ActivatorUtilities.CreateInstance<Infrastructure.Polling.SftpPoller>(sp);
        });
        services.AddSingleton<Infrastructure.Polling.MultiProtocolPoller>(sp =>
        {
            var features = sp.GetRequiredService<IOptions<PipelineFeaturesOptions>>().Value;
            var pollers = new List<Abstractions.IFilePoller>();
            if (features.EnableLocalPoller)
            {
                pollers.Add(sp.GetRequiredService<Infrastructure.Polling.LocalDirectoryPoller>());
            }
            if (features.EnableFtpPoller)
            {
                pollers.Add(sp.GetRequiredService<Infrastructure.Polling.FtpPoller>());
            }
            if (features.EnableSftpPoller)
            {
                pollers.Add(sp.GetRequiredService<Infrastructure.Polling.SftpPoller>());
            }
            return new Infrastructure.Polling.MultiProtocolPoller(pollers, sp.GetRequiredService<ILogger<Infrastructure.Polling.MultiProtocolPoller>>());
        });
        // Override IFilePoller to composite
        services.AddSingleton<Abstractions.IFilePoller>(sp => sp.GetRequiredService<Infrastructure.Polling.MultiProtocolPoller>());

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
        services.AddOptions<PipelineFeaturesOptions>();
        services.AddOptions<Configuration.RemoteFileSourcesOptions>();
        services.AddSingleton<IValidateOptions<Configuration.RemoteFileSourcesOptions>, Configuration.RemoteFileSourcesOptionsValidator>();
        services.AddOptions<Configuration.DestinationsOptions>();
        services.AddSingleton<IValidateOptions<Configuration.DestinationsOptions>, Configuration.DestinationsOptionsValidator>();
        services.AddOptions<Configuration.RoutingOptions>();
        services.AddSingleton<IValidateOptions<Configuration.RoutingOptions>, Configuration.RoutingOptionsValidator>();
        services.AddOptions<Configuration.TransferOptions>();
        services.AddSingleton<IValidateOptions<Configuration.TransferOptions>, Configuration.TransferOptionsValidator>();
        services.AddOptions<Configuration.IdempotencyOptions>();

        // Idempotency store (choose Redis if enabled)
        services.AddSingleton<Abstractions.IIdempotencyStore>(sp =>
        {
            var redis = sp.GetService<IOptions<RedisOptions>>()?.Value;
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            if (redis is { Enabled: true })
            {
                return new Infrastructure.Idempotency.RedisIdempotencyStore(
                    loggerFactory.CreateLogger<Infrastructure.Idempotency.RedisIdempotencyStore>(),
                    sp.GetRequiredService<IOptionsMonitor<RedisOptions>>()
                );
            }
            return new Infrastructure.Idempotency.InMemoryIdempotencyStore();
        });

        // Secret resolution (dev placeholder). Host layer can replace with Key Vault implementation.
        services.AddSingleton<Abstractions.ISecretResolver, Infrastructure.Secrets.InMemorySecretResolver>();

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

        // Service Bus publisher options (bound in host via configuration)
        services.AddOptions<ServiceBusPublisherOptions>();
        services.AddSingleton<Abstractions.IFileContentPublisher, Infrastructure.Messaging.ServiceBus.AzureServiceBusFileContentPublisher>();

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
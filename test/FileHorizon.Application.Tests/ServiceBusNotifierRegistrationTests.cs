using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Infrastructure.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FileHorizon.Application.Tests;

public class ServiceBusNotifierRegistrationTests
{
    [Fact]
    public void DisabledOptions_ResolvesStubNotifier()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddSingleton<IFileProcessingTelemetry, Infrastructure.Telemetry.FileProcessingTelemetry>();
        services.AddSingleton<IIdempotencyStore>(new Infrastructure.Idempotency.InMemoryIdempotencyStore());
        services.AddSingleton<ISecretResolver>(new Infrastructure.Secrets.InMemorySecretResolver(NullLogger<Infrastructure.Secrets.InMemorySecretResolver>.Instance));
        services.Configure<ServiceBusNotificationOptions>(o => { o.Enabled = false; });

        services.AddSingleton<IFileProcessedNotifier>(sp =>
        {
            var monitor = sp.GetRequiredService<IOptionsMonitor<ServiceBusNotificationOptions>>();
            var telemetry = sp.GetRequiredService<IFileProcessingTelemetry>();
            var idemp = sp.GetRequiredService<IIdempotencyStore>();
            var secret = sp.GetRequiredService<ISecretResolver>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var opts = monitor.CurrentValue;
            if (opts.Enabled && opts.AuthMode == ServiceBusAuthMode.ConnectionString && !string.IsNullOrWhiteSpace(opts.ConnectionSecretRef) && !string.IsNullOrWhiteSpace(opts.EntityName))
            {
                return new ServiceBusFileProcessedNotifier(monitor, secret, idemp, telemetry, loggerFactory.CreateLogger<ServiceBusFileProcessedNotifier>());
            }
            return new StubFileProcessedNotifier(monitor, idemp, telemetry, loggerFactory.CreateLogger<StubFileProcessedNotifier>());
        });

        var provider = services.BuildServiceProvider();
        var notifier = provider.GetRequiredService<IFileProcessedNotifier>();
        Assert.IsType<StubFileProcessedNotifier>(notifier);
    }
}

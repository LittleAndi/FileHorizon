using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Models;
using FileHorizon.Application.Common;
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Infrastructure.FileProcessing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;

namespace FileHorizon.Application.Tests;

public class ServiceRegistrationTests
{
    private static ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection().Build());
        // Minimal logging for DI
        services.AddLogging(b => b.AddDebug().AddConsole());
        // Register application services
        // Register application services first then override publisher with test stub
        services.AddApplicationServices();
        services.AddSingleton<IFileContentPublisher, TestNoopFileContentPublisher>();
        // Override features to control processor selection
        services.AddSingleton<IOptions<PipelineFeaturesOptions>>(Options.Create(new PipelineFeaturesOptions
        {
            EnableLocalPoller = true
        }));
        // Provide required option types with defaults to satisfy other registrations
        services.AddSingleton<IOptions<PollingOptions>>(Options.Create(new PollingOptions()));
        services.AddSingleton<IOptions<RedisOptions>>(Options.Create(new RedisOptions()));
        services.AddSingleton<IOptions<FileSourcesOptions>>(Options.Create(new FileSourcesOptions()));
        services.AddSingleton<IOptions<DestinationsOptions>>(Options.Create(new DestinationsOptions()));
        services.AddSingleton<IOptions<RoutingOptions>>(Options.Create(new RoutingOptions()));
        services.AddSingleton<IOptions<TransferOptions>>(Options.Create(new TransferOptions()));
        services.AddSingleton<IOptions<PipelineOptions>>(Options.Create(new PipelineOptions()));
        services.AddSingleton<IOptions<IdempotencyOptions>>(Options.Create(new IdempotencyOptions()));
        return services.BuildServiceProvider();
    }

    [Fact]
    public void IFileProcessor_Should_Be_Orchestrator_By_Default()
    {
        using var sp = BuildServiceProvider();
        var proc = sp.GetRequiredService<IFileProcessor>();
        Assert.IsType<FileProcessingOrchestrator>(proc);
    }

    // Legacy processor removed; orchestrator is the only implementation
    private sealed class TestNoopFileContentPublisher : IFileContentPublisher
    {
        public Task<Result> PublishAsync(FilePublishRequest request, CancellationToken ct) => Task.FromResult(Result.Success());
    }
}

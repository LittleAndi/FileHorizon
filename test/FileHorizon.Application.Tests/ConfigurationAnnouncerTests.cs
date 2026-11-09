using FileHorizon.Application.Configuration;
using FileHorizon.Application.Infrastructure.Telemetry;
using FileHorizon.Application.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileHorizon.Application.Tests;

public class ConfigurationAnnouncerTests
{
    private readonly CapturingLogger<ConfigurationAnnouncer> _logger = new();

    private ConfigurationAnnouncer Create(
        FileSourcesOptions? local = null,
        RemoteFileSourcesOptions? remote = null,
        RoutingOptions? routing = null,
        DestinationsOptions? destinations = null,
        PipelineFeaturesOptions? features = null,
        PipelineRole role = PipelineRole.All)
        => new(
            new OptionsMonitorStub<FileSourcesOptions>(local ?? new FileSourcesOptions()),
            new OptionsMonitorStub<RemoteFileSourcesOptions>(remote ?? new RemoteFileSourcesOptions()),
            new OptionsMonitorStub<RoutingOptions>(routing ?? new RoutingOptions()),
            new OptionsMonitorStub<DestinationsOptions>(destinations ?? new DestinationsOptions()),
            Options.Create(features ?? new PipelineFeaturesOptions()),
            Options.Create(new PipelineOptions { Role = role }),
            _logger);

    private IEnumerable<CapturingLogger<ConfigurationAnnouncer>.Entry> Starting(string prefix)
        => _logger.Entries.Where(e => e.Message.StartsWith(prefix, StringComparison.Ordinal));

    private static RoutingOptions Rules(params RoutingRuleOptions[] rules) => new() { Rules = [.. rules] };

    [Fact]
    public async Task Start_AnnouncesEnabledSourcesOfEnabledPollersOnly()
    {
        var announcer = Create(
            local: new FileSourcesOptions { Sources = [new FileSourceOptions { Name = "inbox", Path = "/data/in", Pattern = "*.xml", Recursive = true }] },
            remote: new RemoteFileSourcesOptions
            {
                Sftp =
                [
                    new SftpSourceOptions { Name = "partners", Host = "SFTP.example.com", Port = 2222, RemotePath = "/partners", Pattern = "*.*", Recursive = false, PasswordSecretRef = "kv:secret" },
                    new SftpSourceOptions { Name = "off", Host = "h", Enabled = false },
                ],
                Ftp = [new FtpSourceOptions { Name = "ftp-src", Host = "ftp.example.com" }],
            },
            features: new PipelineFeaturesOptions { EnableLocalPoller = true, EnableSftpPoller = true, EnableFtpPoller = false });

        await announcer.StartAsync(CancellationToken.None);

        var sources = Starting("Source configured").ToList();
        Assert.Equal(2, sources.Count);

        var local = sources.Single(e => (string?)e.State["Source"] == "inbox");
        Assert.Equal(LogLevel.Information, local.Level);
        Assert.Equal("local", local.State["Protocol"]);
        Assert.Equal("/data/in", local.State["Root"]);
        Assert.Equal("*.xml", local.State["Pattern"]);
        Assert.Equal(true, local.State["Recursive"]);

        var sftp = sources.Single(e => (string?)e.State["Source"] == "partners");
        Assert.Equal("sftp", sftp.State["Protocol"]);
        Assert.Equal("sftp://sftp.example.com:2222/partners", sftp.State["Root"]);
        Assert.Equal(false, sftp.State["Recursive"]);
        Assert.DoesNotContain(_logger.Entries, e => e.Message.Contains("kv:secret"));
    }

    [Fact]
    public async Task Start_AnnouncesRulesInOrderWithResolvedEntity()
    {
        var announcer = Create(
            routing: Rules(
                new RoutingRuleOptions { Name = "ordrsp", Protocol = "sftp", PathGlob = "**/ORDRSP/*.xml", Destinations = ["OrdersBus"] },
                new RoutingRuleOptions { Name = "archive", PathRegex = ".*", Destinations = ["Archive"] },
                new RoutingRuleOptions { Name = "local", PathGlob = "**/*", Destinations = ["Outbox"] },
                new RoutingRuleOptions { Name = "remote", PathGlob = "**/*", Destinations = ["Partner"] }),
            destinations: new DestinationsOptions
            {
                ServiceBus = [new ServiceBusDestinationOptions { Name = "ordersbus", EntityName = "orders-in" }],
                AzureBlob = [new AzureBlobDestinationOptions { Name = "Archive", ContainerName = "archive", RootPathPrefix = "/ingest/" }],
                Local = [new LocalDestinationOptions { Name = "Outbox", RootPath = "/data/out" }],
                Sftp = [new SftpDestinationOptions { Name = "Partner", Host = "dest.example.com", RootPath = "/drop", PasswordSecretRef = "kv:dest" }],
            });

        await announcer.StartAsync(CancellationToken.None);

        var rules = Starting("Rule configured").ToList();
        Assert.Equal(["ordrsp", "archive", "local", "remote"], rules.Select(e => (string?)e.State["Rule"]));
        Assert.Equal([0, 1, 2, 3], rules.Select(e => (int)e.State["Order"]!));

        Assert.Equal("sftp", rules[0].State["Protocol"]);
        Assert.Equal("**/ORDRSP/*.xml", rules[0].State["PathGlob"]);
        Assert.Equal("OrdersBus", rules[0].State["Destination"]);
        Assert.Equal("ServiceBus", rules[0].State["Kind"]);
        Assert.Equal("orders-in", rules[0].State["Entity"]);

        Assert.Equal("AzureBlob", rules[1].State["Kind"]);
        Assert.Equal("archive/ingest", rules[1].State["Entity"]);
        Assert.Equal("/data/out", rules[2].State["Entity"]);
        Assert.Equal("sftp://dest.example.com:22/drop", rules[3].State["Entity"]);

        Assert.DoesNotContain(_logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.DoesNotContain(_logger.Entries, e => e.Message.Contains("kv:dest"));
    }

    [Fact]
    public async Task Start_WarnsAboutRuleSettingsTheRouterDoesNotHonour()
    {
        var announcer = Create(
            routing: Rules(new RoutingRuleOptions { Name = "r1", SourceName = "partners", Destinations = ["Typo", "Second"] }));

        await announcer.StartAsync(CancellationToken.None);

        var rule = Assert.Single(Starting("Rule configured"));
        Assert.Equal("Unknown", rule.State["Kind"]);
        Assert.Null(rule.State["Entity"]);

        var warnings = _logger.Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToList();
        Assert.Equal(3, warnings.Count);
        Assert.Contains(warnings, m => m.Contains("not a configured destination"));
        Assert.Contains(warnings, m => m.Contains("only the first (Typo) is used"));
        Assert.Contains(warnings, m => m.Contains("does not enforce"));
    }

    [Fact]
    public async Task Start_WarnsWhenNoRulesAreConfigured()
    {
        await Create().StartAsync(CancellationToken.None);

        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("No routing rules configured", entry.Message);
    }

    [Theory]
    [InlineData(PipelineRole.Poller, true, false)]
    [InlineData(PipelineRole.Worker, false, true)]
    [InlineData(PipelineRole.All, true, true)]
    public async Task Start_AnnouncesOnlyWhatTheRoleDoes(PipelineRole role, bool sources, bool rules)
    {
        var announcer = Create(
            local: new FileSourcesOptions { Sources = [new FileSourceOptions { Name = "inbox", Path = "/in" }] },
            routing: Rules(new RoutingRuleOptions { Name = "r", PathGlob = "**/*", Destinations = ["Outbox"] }),
            destinations: new DestinationsOptions { Local = [new LocalDestinationOptions { Name = "Outbox", RootPath = "/out" }] },
            role: role);

        await announcer.StartAsync(CancellationToken.None);

        Assert.Equal(sources, Starting("Source configured").Any());
        Assert.Equal(rules, Starting("Rule configured").Any());
    }

    [Fact]
    public async Task Start_InvalidSection_IsSkippedWithWarningInsteadOfFailingStartup()
    {
        var announcer = new ConfigurationAnnouncer(
            new OptionsMonitorStub<FileSourcesOptions>(new FileSourcesOptions()),
            new OptionsMonitorStub<RemoteFileSourcesOptions>(new RemoteFileSourcesOptions()),
            new ThrowingOptionsMonitor<RoutingOptions>(),
            new OptionsMonitorStub<DestinationsOptions>(new DestinationsOptions()),
            Options.Create(new PipelineFeaturesOptions()),
            Options.Create(new PipelineOptions()),
            _logger);

        await announcer.StartAsync(CancellationToken.None);

        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("Not announcing RoutingOptions configuration", entry.Message);
    }

    [Fact]
    public async Task RoutingReload_AnnouncesRulesAgain_UntilStopped()
    {
        var routing = new ReloadableOptionsMonitor<RoutingOptions>(Rules(new RoutingRuleOptions { Name = "v1", PathGlob = "**/*", Destinations = ["Outbox"] }));
        var announcer = new ConfigurationAnnouncer(
            new OptionsMonitorStub<FileSourcesOptions>(new FileSourcesOptions()),
            new OptionsMonitorStub<RemoteFileSourcesOptions>(new RemoteFileSourcesOptions()),
            routing,
            new OptionsMonitorStub<DestinationsOptions>(new DestinationsOptions { Local = [new LocalDestinationOptions { Name = "Outbox", RootPath = "/out" }] }),
            Options.Create(new PipelineFeaturesOptions()),
            Options.Create(new PipelineOptions()),
            _logger);

        await announcer.StartAsync(CancellationToken.None);
        routing.Reload(Rules(new RoutingRuleOptions { Name = "v2", PathGlob = "**/*", Destinations = ["Outbox"] }));
        await announcer.StopAsync(CancellationToken.None);
        routing.Reload(Rules(new RoutingRuleOptions { Name = "v3", PathGlob = "**/*", Destinations = ["Outbox"] }));

        Assert.Equal(["v1", "v2"], Starting("Rule configured").Select(e => (string?)e.State["Rule"]));
    }

    private sealed class ReloadableOptionsMonitor<T>(T value) : IOptionsMonitor<T> where T : class
    {
        private readonly List<Action<T, string?>> _listeners = [];
        public T CurrentValue { get; private set; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<T, string?> listener)
        {
            _listeners.Add(listener);
            return new Subscription(() => _listeners.Remove(listener));
        }
        public void Reload(T next)
        {
            CurrentValue = next;
            foreach (var l in _listeners.ToList()) l(next, Options.DefaultName);
        }
        private sealed class Subscription(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }

    private sealed class ThrowingOptionsMonitor<T> : IOptionsMonitor<T> where T : class
    {
        public T CurrentValue => throw new OptionsValidationException(Options.DefaultName, typeof(T), ["bad rule"]);
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}

using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Configuration;
using FileHorizon.Application.Infrastructure.Redis;
using FileHorizon.Application.Models;
using FileHorizon.Application.Validation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace FileHorizon.Application.Tests;

/// <summary>
/// Integration-style tests against a real Redis instance (expected at localhost:6379 unless REDIS_TEST_CONN is set).
/// If Redis is unavailable these tests become no-ops (return early) so regular test runs do not fail on missing infra.
/// </summary>
public class RedisFileEventQueueTests : IClassFixture<RedisTestFixture>
{
    private readonly RedisTestFixture _fixture;
    private static FileEvent NewEvent(string name) => new(
        Id: Guid.NewGuid().ToString(),
        Metadata: new FileMetadata($"/tmp/{name}.dat", 10, DateTimeOffset.UtcNow, "sha256", null),
        DiscoveredAtUtc: DateTimeOffset.UtcNow,
        Protocol: "local",
        DestinationPath: $"/dest/{name}.dat");

    public RedisFileEventQueueTests(RedisTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Enqueue_Then_Dequeue_Single_Item()
    {
        if (!_fixture.Available) return; // skip silently if redis not present
        await using var queue = _fixture.NewQueue();
        var ev = NewEvent("one");
        var r = await queue.EnqueueAsync(ev, CancellationToken.None);
        Assert.True(r.IsSuccess);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await foreach (var item in queue.DequeueAsync(cts.Token))
        {
            Assert.Equal(ev.Id, item.Id);
            Assert.Equal(ev.Metadata.SourcePath, item.Metadata.SourcePath);
            break; // only need first
        }
    }

    [Fact]
    public async Task Enqueue_Multiple_Preserves_Order()
    {
        if (!_fixture.Available) return;
        await using var queue = _fixture.NewQueue();
        var ev1 = NewEvent("one");
        var ev2 = NewEvent("two");
        var ev3 = NewEvent("three");
        await queue.EnqueueAsync(ev1, CancellationToken.None);
        await queue.EnqueueAsync(ev2, CancellationToken.None);
        await queue.EnqueueAsync(ev3, CancellationToken.None);

        var received = new List<FileEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await foreach (var item in queue.DequeueAsync(cts.Token))
        {
            received.Add(item);
            if (received.Count == 3) break;
        }
        Assert.Equal(new[] { ev1.Id, ev2.Id, ev3.Id }, received.Select(r => r.Id).ToArray());
    }

    [Fact]
    public async Task Cancellation_Stops_Dequeue()
    {
        if (!_fixture.Available) return;
        await using var queue = _fixture.NewQueue();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var enumerated = false;
        await foreach (var _ in queue.DequeueAsync(cts.Token))
        {
            enumerated = true;
        }
        Assert.False(enumerated);
    }

    [Fact]
    public async Task Enqueue_Invalid_Event_Should_Return_Failure()
    {
        if (!_fixture.Available) return;
        await using var queue = _fixture.NewQueue();
        var invalid = new FileEvent("", new FileMetadata("", -1, DateTimeOffset.UtcNow, "invalidhash", null), DateTimeOffset.UtcNow, "local", "");
        var r = await queue.EnqueueAsync(invalid, CancellationToken.None);
        Assert.True(r.IsFailure);
    }

    [Fact]
    public async Task Dequeue_Respects_Cancellation_Promptly()
    {
        if (!_fixture.Available) return;
        await using var queue = _fixture.NewQueue();
        using var cts = new CancellationTokenSource();
        var started = Task.Run(async () =>
        {
            await foreach (var _ in queue.DequeueAsync(cts.Token))
            {
                // we don't expect any items; break if somehow produced
                break;
            }
        });
        // Cancel shortly after start
        await Task.Delay(50);
        cts.Cancel();
        // Should complete quickly (<< ReadBlockMilliseconds upper bound)
        var completed = await Task.WhenAny(started, Task.Delay(2000));
        Assert.Equal(started, completed);
    }
}

public sealed class RedisTestFixture : IAsyncLifetime
{
    private readonly string _connectionString;
    public bool Available { get; private set; }
    private ConnectionMultiplexer? _probeConnection;

    public RedisTestFixture()
    {
        _connectionString = Environment.GetEnvironmentVariable("REDIS_TEST_CONN")?.Trim() ?? "localhost:6379";
    }

    public async Task InitializeAsync()
    {
        try
        {
            _probeConnection = await ConnectionMultiplexer.ConnectAsync(_connectionString);
            Available = _probeConnection.IsConnected;
        }
        catch
        {
            Available = false; // Redis not available; tests will be skipped silently.
        }
    }

    public async Task DisposeAsync()
    {
        if (_probeConnection is not null)
        {
            await _probeConnection.CloseAsync();
            _probeConnection.Dispose();
        }
    }

    public RedisFileEventQueue NewQueue()
    {
        if (!Available) throw new InvalidOperationException("Redis not available - NewQueue should not be called.");
        var opts = new RedisOptions
        {
            Enabled = true,
            ConnectionString = _connectionString,
            StreamName = $"fh:test:stream:{Guid.NewGuid():N}",
            ConsumerGroup = "fh-test-group",
            ConsumerNamePrefix = "fh-test",
            ReadBatchSize = 10,
            ReadBlockMilliseconds = 100, // keep tests snappy
            CreateStreamIfMissing = true
        };
        var validator = new BasicFileEventValidator();
        return new RedisFileEventQueue(
            NullLogger<RedisFileEventQueue>.Instance,
            validator,
            Options.Create(opts));
    }
}

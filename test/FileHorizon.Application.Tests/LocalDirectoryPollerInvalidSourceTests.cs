using FileHorizon.Application.Configuration;
using FileHorizon.Application.Infrastructure.Polling;
using FileHorizon.Application.Infrastructure.Queue;
using FileHorizon.Application.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileHorizon.Application.Tests;

public class LocalDirectoryPollerInvalidSourceTests
{

    [Fact]
    public async Task InvalidSource_DisabledAndSkippedSubsequentPolls()
    {
        var validator = new Validation.BasicFileEventValidator();
        var queue = new InMemoryFileEventQueue(new NullLogger<InMemoryFileEventQueue>(), validator);
        var missingPath = Path.Combine(Path.GetTempPath(), "fh-missing-" + Guid.NewGuid().ToString("N"));
        // Do not create directory
        var sources = new FileSourcesOptions
        {
            Sources = new List<FileSourceOptions>
            {
                new() { Name = "missing", Path = missingPath, Pattern = "*.*" }
            }
        };
        var monitor = new OptionsMonitorStub<FileSourcesOptions>(sources);
        var poller = new LocalDirectoryPoller(queue, new NullLogger<LocalDirectoryPoller>(), monitor);

        // First poll should warn and disable
        var r1 = await poller.PollAsync(CancellationToken.None);
        Assert.True(r1.IsSuccess);

        // Create directory after disable; poll again should still skip because config unchanged
        Directory.CreateDirectory(missingPath);
        var r2 = await poller.PollAsync(CancellationToken.None);
        Assert.True(r2.IsSuccess);

        // Queue should be empty (no files enqueued)
        var drained = queue.TryDrain(10);
        Assert.Empty(drained);

        // Trigger config change to re-enable: update monitor CurrentValue
        monitor.Set(new FileSourcesOptions
        {
            Sources = new List<FileSourceOptions>
            {
                new() { Name = "missing", Path = missingPath, Pattern = "*.*", MinStableSeconds = 0 }
            }
        });
        // Instantiate a fresh queue and poller to simulate clean reconfiguration (clears disabled set via ctor OnChange subscription not used in stub)
        queue = new InMemoryFileEventQueue(new NullLogger<InMemoryFileEventQueue>(), validator);
        poller = new LocalDirectoryPoller(queue, new NullLogger<LocalDirectoryPoller>(), monitor);

        // Create a file and poll again
        var filePath = Path.Combine(missingPath, "test.txt");
        await File.WriteAllTextAsync(filePath, "data");
        var r3 = await poller.PollAsync(CancellationToken.None);
        Assert.True(r3.IsSuccess);
        drained = queue.TryDrain(10);
        Assert.True(drained.Count == 1, $"Expected 1 drained event, got {drained.Count}");

        Directory.Delete(missingPath, true);
    }
}

using FileHorizon.Application.Infrastructure.Idempotency;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileHorizon.Application.Tests;

public class FileBackedIdempotencyStoreTests
{
    private static string NewStorePath()
        => Path.Combine(Path.GetTempPath(), "fh-idemp-tests", Guid.NewGuid().ToString("N"), "idempotency.jsonl");

    private static FileBackedIdempotencyStore CreateStore(string path)
        => new(path, NullLogger<FileBackedIdempotencyStore>.Instance);

    private static void Cleanup(string path)
    {
        try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { }
    }

    [Fact]
    public async Task Marks_SurviveReload()
    {
        var path = NewStorePath();
        try
        {
            using (var store = CreateStore(path))
            {
                Assert.True(await store.TryMarkProcessedAsync("k1", null, CancellationToken.None));
                Assert.True(await store.TryMarkProcessedAsync("k2", null, CancellationToken.None));
            }

            using var reloaded = CreateStore(path);
            Assert.True(await reloaded.IsProcessedAsync("k1", CancellationToken.None));
            Assert.True(await reloaded.IsProcessedAsync("k2", CancellationToken.None));
            Assert.False(await reloaded.TryMarkProcessedAsync("k1", null, CancellationToken.None));
            Assert.False(await reloaded.IsProcessedAsync("k3", CancellationToken.None));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task ExpiredEntry_IsNotHonoredAfterReload()
    {
        var path = NewStorePath();
        try
        {
            using (var store = CreateStore(path))
            {
                await store.TryMarkProcessedAsync("short", TimeSpan.FromMilliseconds(30), CancellationToken.None);
                await store.TryMarkProcessedAsync("forever", null, CancellationToken.None);
            }
            await Task.Delay(100);

            using var reloaded = CreateStore(path);
            Assert.False(await reloaded.IsProcessedAsync("short", CancellationToken.None));
            Assert.True(await reloaded.IsProcessedAsync("forever", CancellationToken.None));
            Assert.True(await reloaded.TryMarkProcessedAsync("short", null, CancellationToken.None));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task CorruptLastLine_IsSkipped_RemainingKeysLoad()
    {
        var path = NewStorePath();
        try
        {
            using (var store = CreateStore(path))
            {
                await store.TryMarkProcessedAsync("k1", null, CancellationToken.None);
            }
            // Simulate a torn write after a crash.
            File.AppendAllText(path, "{\"k\":\"k2\",\"e\":nu");

            using var reloaded = CreateStore(path);
            Assert.True(await reloaded.IsProcessedAsync("k1", CancellationToken.None));
            Assert.False(await reloaded.IsProcessedAsync("k2", CancellationToken.None));
            // The store must still accept new marks after loading a corrupt file.
            Assert.True(await reloaded.TryMarkProcessedAsync("k3", null, CancellationToken.None));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task ParallelMarks_SameKey_ExactlyOneWins()
    {
        var path = NewStorePath();
        try
        {
            using var store = CreateStore(path);
            var results = await Task.WhenAll(Enumerable.Range(0, 16)
                .Select(_ => Task.Run(() => store.TryMarkProcessedAsync("contested", null, CancellationToken.None))));

            Assert.Equal(1, results.Count(r => r));
        }
        finally { Cleanup(path); }
    }
}

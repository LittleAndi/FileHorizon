using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Infrastructure.Idempotency;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileHorizon.Application.Tests;

public class IdempotencyImportTests
{
    // A key in the documented shape, so the tests fail if importing ever mangles a real marker.
    private const string RealKey = "fh:idemp:v2:sftp://sftp.example.com:22/upload/a.zip|1234|2026-07-14T09:13:22.0000000+00:00";

    private static string NewFilePath()
        => Path.Combine(Path.GetTempPath(), "fh-idemp-import-tests", Guid.NewGuid().ToString("N"), "idempotency.jsonl");

    private static IdempotencyImporter CreateImporter()
        => new(NullLogger<IdempotencyImporter>.Instance);

    private static void Cleanup(string path)
    {
        try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { }
    }

    private static void WriteLines(string path, params string[] lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
    }

    private static string Line(string key, DateTimeOffset? expiry = null)
        => expiry is null
            ? $"{{\"k\":\"{key}\",\"e\":null}}"
            : $"{{\"k\":\"{key}\",\"e\":\"{expiry:O}\"}}";

    [Fact]
    public async Task ImportAsync_MovesMarkersWrittenByTheFileStoreIntoAnotherStore()
    {
        // The point of importing is that a marker written on the old deployment suppresses the same file on
        // the new one, so the key must survive the move byte for byte.
        var path = NewFilePath();
        try
        {
            using (var fileStore = new FileBackedIdempotencyStore(path, NullLogger<FileBackedIdempotencyStore>.Instance))
            {
                Assert.True(await fileStore.TryMarkProcessedAsync(RealKey, null, CancellationToken.None));
            }

            var target = new InMemoryIdempotencyStore();
            var result = await CreateImporter().ImportAsync(new IdempotencyImportRequest(path), target, CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.Equal(1, result.Value!.Keys);
            Assert.Equal(1, result.Value.Imported);
            Assert.Equal(0, result.Value.AlreadyPresent);
            Assert.Equal(0, result.Value.Failed);
            Assert.True(await target.IsProcessedAsync(RealKey, CancellationToken.None));
            Assert.Equal(RealKey, Assert.Single(result.Value.SampleKeys));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task ImportAsync_DryRun_WritesNothing()
    {
        var path = NewFilePath();
        try
        {
            WriteLines(path, Line(RealKey));
            var target = new InMemoryIdempotencyStore();

            var result = await CreateImporter().ImportAsync(
                new IdempotencyImportRequest(path, DryRun: true), target, CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.Equal(1, result.Value!.Keys);
            Assert.Equal(0, result.Value.Imported);
            Assert.False(await target.IsProcessedAsync(RealKey, CancellationToken.None));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task ImportAsync_PermanentMarker_IsWrittenWithoutTtl()
    {
        var path = NewFilePath();
        try
        {
            WriteLines(path, Line("k1"));
            var target = new RecordingStore();

            var result = await CreateImporter().ImportAsync(new IdempotencyImportRequest(path), target, CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.Null(Assert.Single(target.Marks).Ttl);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task ImportAsync_UnexpiredMarker_KeepsTheLifetimeItHadLeft()
    {
        // Retention is the one thing that is per-store, so an expiring marker must arrive with the time it
        // has left rather than being renewed to a full TTL or made permanent.
        var path = NewFilePath();
        try
        {
            var remaining = TimeSpan.FromHours(3);
            WriteLines(path, Line("k1", DateTimeOffset.UtcNow + remaining));
            var target = new RecordingStore();

            var result = await CreateImporter().ImportAsync(new IdempotencyImportRequest(path), target, CancellationToken.None);

            Assert.True(result.IsSuccess);
            var ttl = Assert.Single(target.Marks).Ttl;
            Assert.NotNull(ttl);
            Assert.True((remaining - ttl!.Value).Duration() < TimeSpan.FromMinutes(1), $"ttl was {ttl}");
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task ImportAsync_ExpiredMarkers_AreDroppedNotResurrected()
    {
        var path = NewFilePath();
        try
        {
            WriteLines(path,
                Line("expired", DateTimeOffset.UtcNow.AddMinutes(-1)),
                Line("live"));
            var target = new InMemoryIdempotencyStore();

            var result = await CreateImporter().ImportAsync(new IdempotencyImportRequest(path), target, CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.Equal(2, result.Value!.Keys);
            Assert.Equal(1, result.Value.Imported);
            Assert.Equal(1, result.Value.Expired);
            Assert.False(await target.IsProcessedAsync("expired", CancellationToken.None));
            Assert.True(await target.IsProcessedAsync("live", CancellationToken.None));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task ImportAsync_TornLine_IsSkippedAndCounted()
    {
        var path = NewFilePath();
        try
        {
            WriteLines(path, Line("k1"), "{\"k\":\"k2\",\"e\":nu");
            var target = new InMemoryIdempotencyStore();

            var result = await CreateImporter().ImportAsync(new IdempotencyImportRequest(path), target, CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.Equal(2, result.Value!.Lines);
            Assert.Equal(1, result.Value.Keys);
            Assert.Equal(1, result.Value.Imported);
            Assert.Equal(1, result.Value.Unreadable);
            Assert.True(await target.IsProcessedAsync("k1", CancellationToken.None));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task ImportAsync_RepeatedKey_IsWrittenOnceWithItsLastRecordedExpiry()
    {
        // The file is append-only, so a key re-marked after its TTL lapsed appears twice; the later line is
        // the current truth, exactly as the file store resolves it when loading.
        var path = NewFilePath();
        try
        {
            WriteLines(path,
                Line("k1", DateTimeOffset.UtcNow.AddMinutes(-1)),
                Line("k1"));
            var target = new RecordingStore();

            var result = await CreateImporter().ImportAsync(new IdempotencyImportRequest(path), target, CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.Equal(2, result.Value!.Lines);
            Assert.Equal(1, result.Value.Keys);
            Assert.Equal(0, result.Value.Expired);
            Assert.Null(Assert.Single(target.Marks).Ttl);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task ImportAsync_RunTwice_ReportsSecondPassAsAlreadyPresent()
    {
        var path = NewFilePath();
        try
        {
            WriteLines(path, Line(RealKey));
            var target = new InMemoryIdempotencyStore();
            var importer = CreateImporter();

            await importer.ImportAsync(new IdempotencyImportRequest(path), target, CancellationToken.None);
            var second = await importer.ImportAsync(new IdempotencyImportRequest(path), target, CancellationToken.None);

            Assert.True(second.IsSuccess);
            Assert.Equal(0, second.Value!.Imported);
            Assert.Equal(1, second.Value.AlreadyPresent);
            Assert.Equal(0, second.Value.Failed);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task ImportAsync_StoreThatSwallowsWrites_ReportsFailedRatherThanAlreadyPresent()
    {
        // The stores fail open, so an outage returns false from TryMarkProcessedAsync just like an existing
        // marker does. Reporting that as "already present" would make a lost migration look like a clean one.
        var path = NewFilePath();
        try
        {
            WriteLines(path, Line("k1"), Line("k2"));

            var result = await CreateImporter().ImportAsync(
                new IdempotencyImportRequest(path), new FailOpenStore(), CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.Equal(0, result.Value!.Imported);
            Assert.Equal(0, result.Value.AlreadyPresent);
            Assert.Equal(2, result.Value.Failed);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task ImportAsync_MissingFile_Fails()
    {
        var result = await CreateImporter().ImportAsync(
            new IdempotencyImportRequest(NewFilePath()), new InMemoryIdempotencyStore(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("File.NotFound", result.Error.Code);
    }

    [Fact]
    public async Task ImportAsync_EmptyPath_Fails()
    {
        var result = await CreateImporter().ImportAsync(
            new IdempotencyImportRequest("  "), new InMemoryIdempotencyStore(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Validation.Invalid", result.Error.Code);
    }

    [Fact]
    public async Task ImportAsync_ThrowingStore_ReportsProgressWithTheFailure()
    {
        var path = NewFilePath();
        try
        {
            WriteLines(path, Line("k1"), Line("k2"));

            var result = await CreateImporter().ImportAsync(
                new IdempotencyImportRequest(path), new ThrowingStore(failOnCall: 2), CancellationToken.None);

            Assert.True(result.IsFailure);
            Assert.Equal("Idempotency.ImportFailed", result.Error.Code);
            Assert.Contains("after 1 marker(s)", result.Error.Message);
        }
        finally { Cleanup(path); }
    }

    private sealed class RecordingStore : IIdempotencyStore
    {
        public List<(string Key, TimeSpan? Ttl)> Marks { get; } = [];

        public Task<bool> IsProcessedAsync(string key, CancellationToken ct)
            => Task.FromResult(Marks.Any(m => m.Key == key));

        public Task<bool> TryMarkProcessedAsync(string key, TimeSpan? ttl, CancellationToken ct)
        {
            Marks.Add((key, ttl));
            return Task.FromResult(true);
        }
    }

    /// <summary>Mimics a store outage under the fail-open contract: nothing is written, nothing throws.</summary>
    private sealed class FailOpenStore : IIdempotencyStore
    {
        public Task<bool> IsProcessedAsync(string key, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> TryMarkProcessedAsync(string key, TimeSpan? ttl, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class ThrowingStore(int failOnCall) : IIdempotencyStore
    {
        private int _calls;

        public Task<bool> IsProcessedAsync(string key, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> TryMarkProcessedAsync(string key, TimeSpan? ttl, CancellationToken ct)
        {
            if (++_calls >= failOnCall) throw new InvalidOperationException("store is down");
            return Task.FromResult(true);
        }
    }
}

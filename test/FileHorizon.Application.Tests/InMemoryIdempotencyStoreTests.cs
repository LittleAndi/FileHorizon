using FileHorizon.Application.Infrastructure.Idempotency;

namespace FileHorizon.Application.Tests;

public class InMemoryIdempotencyStoreTests
{
    [Fact]
    public async Task TryMark_FirstCallTrue_SecondCallFalse()
    {
        var store = new InMemoryIdempotencyStore();

        Assert.True(await store.TryMarkProcessedAsync("k1", null, CancellationToken.None));
        Assert.False(await store.TryMarkProcessedAsync("k1", null, CancellationToken.None));
    }

    [Fact]
    public async Task IsProcessed_FalseBeforeMark_TrueAfterMark()
    {
        var store = new InMemoryIdempotencyStore();

        Assert.False(await store.IsProcessedAsync("k1", CancellationToken.None));
        await store.TryMarkProcessedAsync("k1", null, CancellationToken.None);
        Assert.True(await store.IsProcessedAsync("k1", CancellationToken.None));
    }

    [Fact]
    public async Task NullTtl_NeverExpires()
    {
        var store = new InMemoryIdempotencyStore();
        await store.TryMarkProcessedAsync("k1", null, CancellationToken.None);

        await Task.Delay(50);
        Assert.True(await store.IsProcessedAsync("k1", CancellationToken.None));
        Assert.False(await store.TryMarkProcessedAsync("k1", null, CancellationToken.None));
    }

    [Fact]
    public async Task ExpiredTtl_EntryIsNotProcessed_AndCanBeRemarked()
    {
        var store = new InMemoryIdempotencyStore();
        await store.TryMarkProcessedAsync("k1", TimeSpan.FromMilliseconds(30), CancellationToken.None);

        await Task.Delay(100);
        Assert.False(await store.IsProcessedAsync("k1", CancellationToken.None));
        Assert.True(await store.TryMarkProcessedAsync("k1", null, CancellationToken.None));
        Assert.True(await store.IsProcessedAsync("k1", CancellationToken.None));
    }
}

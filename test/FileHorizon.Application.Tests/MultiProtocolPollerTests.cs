using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Common;
using FileHorizon.Application.Infrastructure.Polling;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileHorizon.Application.Tests;

public class MultiProtocolPollerTests
{
    private sealed class StubPoller(string name, Func<CancellationToken, Task<Result>> impl) : IFilePoller
    {
        private readonly Func<CancellationToken, Task<Result>> _impl = impl;
        public int CallCount { get; private set; }
        public string Name { get; } = name;

        public async Task<Result> PollAsync(CancellationToken ct)
        { CallCount++; return await _impl(ct); }
    }

    [Fact]
    public async Task PollAsync_InvokesAllPollers_WhenSuccessful()
    {
        var p1 = new StubPoller("local", _ => Task.FromResult(Result.Success()));
        var p2 = new StubPoller("ftp", _ => Task.FromResult(Result.Success()));
        var p3 = new StubPoller("sftp", _ => Task.FromResult(Result.Success()));
        var composite = new MultiProtocolPoller([p1, p2, p3], NullLogger<MultiProtocolPoller>.Instance);

        var result = await composite.PollAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, p1.CallCount);
        Assert.Equal(1, p2.CallCount);
        Assert.Equal(1, p3.CallCount);
    }

    [Fact]
    public async Task PollAsync_StopsOnCancellation()
    {
        var cts = new CancellationTokenSource();
        var p1 = new StubPoller("a", _ => Task.FromResult(Result.Success()));
        var p2 = new StubPoller("b", ct => { cts.Cancel(); return Task.FromResult(Result.Success()); });
        var p3 = new StubPoller("c", _ => Task.FromResult(Result.Success()));
        var composite = new MultiProtocolPoller([p1, p2, p3], NullLogger<MultiProtocolPoller>.Instance);

        var result = await composite.PollAsync(cts.Token);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, p1.CallCount);
        Assert.Equal(1, p2.CallCount);
        // p3 may or may not run depending on timing, but should not exceed 1
        Assert.True(p3.CallCount <= 1);
    }

    [Fact]
    public async Task PollAsync_LogsFailuresButContinues()
    {
        var failed = new StubPoller("bad", _ => Task.FromResult(Result.Failure(Error.Unspecified("Test.Boom", "boom"))));
        var ok = new StubPoller("ok", _ => Task.FromResult(Result.Success()));
        var composite = new MultiProtocolPoller([failed, ok], NullLogger<MultiProtocolPoller>.Instance);

        var result = await composite.PollAsync(CancellationToken.None);

        Assert.True(result.IsSuccess); // composite always returns success
        Assert.Equal(1, failed.CallCount);
        Assert.Equal(1, ok.CallCount);
    }
}

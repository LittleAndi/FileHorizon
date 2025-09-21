using Microsoft.Extensions.Options;

namespace FileHorizon.Application.Tests.TestSupport;

/// <summary>
/// Minimal IOptionsMonitor<T> test stub supporting a mutable CurrentValue.
/// </summary>
internal sealed class OptionsMonitorStub<T> : IOptionsMonitor<T>
{
    public OptionsMonitorStub(T value) => CurrentValue = value;
    public T CurrentValue { get; private set; }
    public T Get(string? name) => CurrentValue;
    public IDisposable OnChange(Action<T, string?> listener) => new Noop();
    private sealed class Noop : IDisposable { public void Dispose() { } }
    public void Set(T value) => CurrentValue = value;
}

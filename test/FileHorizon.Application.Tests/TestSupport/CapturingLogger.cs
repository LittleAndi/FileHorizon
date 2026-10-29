using Microsoft.Extensions.Logging;

namespace FileHorizon.Application.Tests.TestSupport;

/// <summary>
/// Records every log entry with its level, rendered message and structured state, for asserting on log output.
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public sealed record Entry(LogLevel Level, string Message, IReadOnlyDictionary<string, object?> State);

    public List<Entry> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var values = state as IEnumerable<KeyValuePair<string, object?>> ?? [];
        Entries.Add(new Entry(logLevel, formatter(state, exception), values.ToDictionary(kv => kv.Key, kv => kv.Value)));
    }
}

namespace FileHorizon.Application.Infrastructure.Idempotency;

/// <summary>
/// Records why the idempotency store registration ended up on the store it did.
/// </summary>
/// <remarks>
/// The registration deliberately falls back instead of failing startup, so a transient store problem
/// degrades the service rather than stopping it, and reports the cause through <c>ILogger</c> only.
/// An operator running a one-off command in an environment without a console sink never sees that log,
/// and a caller that *requires* durability would otherwise have to blame configuration for what may
/// really be a locked file. Capturing the cause here lets the caller name the actual problem.
/// </remarks>
public sealed class IdempotencyStoreDiagnostics
{
    private volatile DurableStoreFallback? _fallback;

    /// <summary>
    /// Non-null when a durable store was explicitly configured but could not be constructed, so the
    /// registration fell back. Null both when nothing durable was configured and when one was opened.
    /// </summary>
    public DurableStoreFallback? Fallback => _fallback;

    public void RecordFallback(string store, Exception cause, string? hint = null)
    {
        ArgumentNullException.ThrowIfNull(cause);
        _fallback = new DurableStoreFallback(store, cause.Message, hint);
    }
}

/// <summary>
/// A durable store that was configured but could not be constructed.
/// </summary>
/// <param name="Store">Description of the configured store, e.g. the file path it would have used.</param>
/// <param name="Reason">Message from the exception that prevented construction.</param>
/// <param name="Hint">Optional next step for whoever is looking at it.</param>
public sealed record DurableStoreFallback(string Store, string Reason, string? Hint);

namespace FileHorizon.Application.Infrastructure.Polling;

/// <summary>
/// Parameters for a one-off idempotency seeding run over a single remote source.
/// </summary>
/// <param name="SourceName">Name of the configured source to scan.</param>
/// <param name="PatternOverride">Optional glob replacing the source's configured Pattern, for partial seeding.</param>
/// <param name="DryRun">When true, count and sample keys are produced but no markers are written.</param>
/// <param name="Ttl">Marker lifetime; null keeps markers indefinitely, matching Idempotency:TtlSeconds=0.</param>
public sealed record IdempotencySeedRequest(
    string SourceName,
    string? PatternOverride = null,
    bool DryRun = false,
    TimeSpan? Ttl = null);

/// <summary>
/// Outcome of an idempotency seeding run.
/// </summary>
/// <param name="SourceName">The source that was scanned.</param>
/// <param name="Pattern">The pattern actually applied.</param>
/// <param name="Scanned">Files matched by the pattern.</param>
/// <param name="Marked">Files newly marked as already transferred (always 0 for a dry run).</param>
/// <param name="AlreadyMarked">Files that already had a marker.</param>
/// <param name="SampleKeys">First few generated keys, for eyeballing before committing to a large run.</param>
public sealed record IdempotencySeedResult(
    string SourceName,
    string Pattern,
    int Scanned,
    int Marked,
    int AlreadyMarked,
    IReadOnlyList<string> SampleKeys);

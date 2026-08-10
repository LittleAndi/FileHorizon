using FileHorizon.Application.Abstractions;
using FileHorizon.Application.Infrastructure.Idempotency;

namespace FileHorizon.Host.Commands;

/// <summary>
/// Shared reporting for the maintenance commands that write idempotency markers and therefore need a
/// store that outlives the process.
/// </summary>
internal static class IdempotencyStoreRequirement
{
    /// <summary>
    /// Explains why a command that requires durability will not run. The store registration lands on the
    /// in-memory store for two very different reasons - nothing durable configured, or something durable
    /// that would not open - and only the first is a configuration problem, so pointing at config either way
    /// would send an operator hunting through appsettings when the real fix is to stop the running service.
    /// </summary>
    /// <param name="services">Built provider, for the recorded fallback cause.</param>
    /// <param name="consequence">What the caller was about to write, and that it was not written.</param>
    public static string DescribeUnusable(IServiceProvider services, string consequence)
    {
        var fallback = services.GetRequiredService<IdempotencyStoreDiagnostics>().Fallback;
        if (fallback is null)
        {
            return $"No durable idempotency store is configured. {consequence} " +
                   "Set Idempotency:DataDirectory (or enable Redis) and try again.";
        }

        var message = $"Configuration selects {fallback.Store}, but it could not be opened: {fallback.Reason} {consequence}";
        return fallback.Hint is null ? message : $"{message} {fallback.Hint}";
    }

    /// <summary>
    /// Names the store markers are going into. Which store took effect is the question an operator moving
    /// between stores actually has, and the selection is silent about it outside the startup log.
    /// </summary>
    public static string Describe(IIdempotencyStore store) => store switch
    {
        FileBackedIdempotencyStore file => $"the file-backed store at {file.FilePath}",
        RedisIdempotencyStore => "the Redis store",
        InMemoryIdempotencyStore => "the in-memory store (nothing survives this process)",
        _ => store.GetType().Name
    };
}

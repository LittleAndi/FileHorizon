using Microsoft.Extensions.Options;

namespace FileHorizon.Application.Configuration;

public sealed class TransferOptionsValidator : IValidateOptions<TransferOptions>
{
    public ValidateOptionsResult Validate(string? name, TransferOptions options)
    {
        if (options is null) return ValidateOptionsResult.Fail("TransferOptions instance is null");

        var errors = new List<string>();
        if (options.MaxConcurrentPerDestination <= 0) errors.Add("Transfer: MaxConcurrentPerDestination must be >= 1");
        if (options.ChunkSizeBytes <= 0) errors.Add("Transfer: ChunkSizeBytes must be > 0");
        if (options.Retry.MaxAttempts <= 0) errors.Add("Transfer: Retry.MaxAttempts must be >= 1");
        if (options.Retry.BackoffBaseMs < 0) errors.Add("Transfer: Retry.BackoffBaseMs must be >= 0");
        if (options.Retry.BackoffMaxMs < options.Retry.BackoffBaseMs) errors.Add("Transfer: Retry.BackoffMaxMs must be >= BackoffBaseMs");

        var algo = options.Checksum.Algorithm?.ToLowerInvariant() ?? "none";
        if (algo is not ("none" or "sha256" or "sha512"))
        {
            errors.Add($"Transfer: Checksum.Algorithm '{options.Checksum.Algorithm}' is not supported (allowed: none|sha256|sha512)");
        }

        return errors.Count > 0 ? ValidateOptionsResult.Fail(errors) : ValidateOptionsResult.Success;
    }
}

using Microsoft.Extensions.Options;

namespace FileHorizon.Application.Configuration;

public sealed class IdempotencyOptionsValidator : IValidateOptions<IdempotencyOptions>
{
    public ValidateOptionsResult Validate(string? name, IdempotencyOptions options)
    {
        if (options is null) return ValidateOptionsResult.Fail("IdempotencyOptions instance is null");

        var errors = new List<string>();

        if (options.TtlSeconds < 0)
        {
            errors.Add($"Idempotency: TtlSeconds must not be negative (use 0 for indefinite retention), but was {options.TtlSeconds}");
        }

        if (options.DataDirectory is not null)
        {
            if (string.IsNullOrWhiteSpace(options.DataDirectory))
            {
                errors.Add("Idempotency: DataDirectory must not be empty or whitespace when set");
            }
            else
            {
                try
                {
                    _ = Path.GetFullPath(options.DataDirectory);
                }
                catch (Exception ex)
                {
                    errors.Add($"Idempotency: DataDirectory '{options.DataDirectory}' is not a valid path ({ex.Message})");
                }
            }
        }

        return errors.Count > 0 ? ValidateOptionsResult.Fail(errors) : ValidateOptionsResult.Success;
    }
}

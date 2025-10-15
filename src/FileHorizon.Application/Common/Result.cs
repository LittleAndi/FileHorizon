namespace FileHorizon.Application.Common;

public readonly struct Result
{
    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public Error Error { get; }

    private Result(bool isSuccess, Error error)
    {
        IsSuccess = isSuccess;
        Error = error;
    }

    public static Result Success() => new(true, Error.None);
    public static Result Failure(Error error)
        => new(false, error == Error.None ? Error.Unspecified("ResultFailureNone", "Attempted to create a failure result with Error.None") : error);

    public override string ToString() => IsSuccess ? "Success" : $"Failure: {Error}";
}

public readonly struct Result<T>
{
    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public T? Value { get; }
    public Error Error { get; }

    private Result(bool isSuccess, T? value, Error error)
    {
        IsSuccess = isSuccess;
        Value = value;
        Error = error;
    }

    public static Result<T> Success(T value) => new(true, value, Error.None);
    public static Result<T> Failure(Error error) => new(false, default, error);

    public override string ToString() => IsSuccess ? $"Success: {Value}" : $"Failure: {Error}";
}

public readonly record struct Error(string Code, string Message)
{
    public static readonly Error None = new("None", string.Empty);

    public static Error Unspecified(string code, string message) => new(code, message);

    public override string ToString() => Code == "None" ? "<none>" : $"{Code}: {Message}";

    // Domain categories (expand as needed)
    public static class File
    {
        public static Error NotFound(string path) => new("File.NotFound", $"File not found: {path}");
        public static readonly Error SizeUnstable = new("File.SizeUnstable", "File size not yet stable");
        public static readonly Error LockUnavailable = new("File.LockUnavailable", "Could not obtain exclusive lock");
    }

    public static class Processing
    {
        public static readonly Error AlreadyProcessed = new("Processing.AlreadyProcessed", "File already processed");
        public static readonly Error ChecksumMismatch = new("Processing.ChecksumMismatch", "Checksum mismatch");
    }

    public static class Validation
    {
        public static Error Invalid(string reason) => new("Validation.Invalid", reason);
        public static readonly Error NullFileEvent = new("Validation.NullFileEvent", "FileEvent was null");
        public static readonly Error EmptyId = new("Validation.EmptyId", "FileEvent Id was null or whitespace");
        public static readonly Error NullMetadata = new("Validation.NullMetadata", "FileEvent.Metadata was null");
        public static readonly Error EmptySourcePath = new("Validation.EmptySourcePath", "File metadata SourcePath was null or whitespace");
        public static readonly Error NegativeSize = new("Validation.NegativeSize", "File size must be >= 0");
    }

    public static class Messaging
    {
        public static readonly Error DestinationEmpty = new("Messaging.DestinationEmpty", "DestinationName must be provided.");
        public static readonly Error ContentEmpty = new("Messaging.ContentEmpty", "Content must not be empty.");
        public static Error PublishTransient(string message) => new("Messaging.PublishTransient", message);
        public static Error PublishError(string message) => new("Messaging.PublishError", message);
    }
}

public static class Guard
{
    public static void AgainstNull(object? value, string paramName)
    {
        if (value is null) throw new ArgumentNullException(paramName);
    }

    public static void AgainstNullOrWhiteSpace(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Value cannot be null or whitespace", paramName);
    }
}
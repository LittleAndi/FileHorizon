namespace FileHorizon.Application.Abstractions;

/// <summary>
/// Abstraction over file processing telemetry emission so domain logic can be unit tested
/// without depending directly on System.Diagnostics.Metrics.
/// </summary>
public interface IFileProcessingTelemetry
{
    void RecordSuccess(string protocol, double elapsedMs);
    void RecordFailure(string protocol, double elapsedMs);
}

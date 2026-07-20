namespace FileHorizon.Application.Models;

public enum DestinationKind
{
    Local = 0,
    Sftp = 1,
    ServiceBus = 2,
    AzureBlob = 3
}

public sealed record DestinationPlan(
    string DestinationName,
    string TargetPath,
    FileWriteOptions Options,
    DestinationKind Kind = DestinationKind.Local,
    bool IsTopic = false);

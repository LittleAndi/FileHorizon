namespace FileHorizon.Application.Models;

public sealed record DestinationPlan(
    string DestinationName,
    string TargetPath,
    FileWriteOptions Options);

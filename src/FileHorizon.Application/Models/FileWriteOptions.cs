namespace FileHorizon.Application.Models;

public sealed record FileWriteOptions(
    bool Overwrite,
    bool ComputeHash,
    string? RenamePattern);

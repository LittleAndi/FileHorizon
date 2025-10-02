namespace FileHorizon.Application.Models;

public sealed record FileReference(
    string Scheme,
    string? Host,
    int? Port,
    string Path,
    string? SourceName);

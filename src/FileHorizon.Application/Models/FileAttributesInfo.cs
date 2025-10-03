namespace FileHorizon.Application.Models;

public sealed record FileAttributesInfo(
    long Size,
    DateTimeOffset LastWriteUtc,
    string? Hash);

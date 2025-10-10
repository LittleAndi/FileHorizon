namespace FileHorizon.Application.Configuration;

public sealed class ContentDetectionOptions
{
    public const string SectionName = "ContentDetection";

    /// <summary>
    /// Enable XML detection heuristics (lightweight, prefix-based).
    /// </summary>
    public bool EnableXml { get; set; } = true;

    /// <summary>
    /// Enable EDIFACT detection heuristics (UNA/UNB/UNH segment sniff).
    /// </summary>
    public bool EnableEdifact { get; set; } = true;

    /// <summary>
    /// Max number of bytes to read for detection (prefix sample). Keep small to avoid overhead.
    /// </summary>
    public int MaxSampleBytes { get; set; } = 4096;

    /// <summary>
    /// Minimum confidence (0-100) required to override an extension-based guess.
    /// </summary>
    public int ConfidenceOverrideThreshold { get; set; } = 60;
}
namespace FileHorizon.Application.Configuration;

public sealed class PollingOptions
{
    public const string SectionName = "Polling";
    public int IntervalMilliseconds { get; set; } = 1000;
    public int BatchReadLimit { get; set; } = 10; // how many queue items to process per cycle
}

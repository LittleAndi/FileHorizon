namespace FileHorizon.Application.Configuration;

public sealed class RoutingOptions
{
    public const string SectionName = "Routing";
    public List<RoutingRuleOptions> Rules { get; set; } = [];
}

public sealed class RoutingRuleOptions
{
    public string Name { get; set; } = string.Empty;
    public string? SourceName { get; set; }
    public string? Protocol { get; set; }
    public string? PathGlob { get; set; }
    public string? PathRegex { get; set; }
    public List<string> Destinations { get; set; } = [];
    public string? RenamePattern { get; set; }
    public bool? Overwrite { get; set; }
}

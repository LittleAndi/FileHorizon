using FileHorizon.Application.Configuration;
using FileHorizon.Application.Infrastructure.Polling;
using FileHorizon.Application.Infrastructure.Processing;

namespace FileHorizon.Application.Tests;

public class RouteMatcherTests
{
    private const string Partners = "sftp://sftp.example.com:22/partners";

    private static RoutingRuleOptions Rule(string? glob = null, string? regex = null, string? protocol = null)
        => new() { Name = "r", PathGlob = glob, PathRegex = regex, Protocol = protocol, Destinations = ["D"] };

    [Theory]
    [InlineData("**/ORDRSP/*.xml", Partners + "/acme/ORDRSP", "*.xml", FolderMatch.Exact)]
    [InlineData("**/ORDRSP/*.xml", Partners + "/acme/ORDRSP", "*.*", FolderMatch.PatternDependent)]
    [InlineData("**/ORDRSP/*.xml", Partners + "/acme/ORDRSP", "*.csv", FolderMatch.None)]
    [InlineData("**/ORDRSP/*.xml", Partners + "/acme/INVOIC", "*.xml", FolderMatch.None)]
    [InlineData("**/ORDRSP/*", Partners + "/acme/ORDRSP", "*.csv", FolderMatch.Exact)]
    [InlineData("**/ORDRSP/ORD_*.xml", Partners + "/acme/ORDRSP", "*.xml", FolderMatch.PatternDependent)]
    [InlineData("**/*", Partners + "/acme/ORDRSP", "*.xml", FolderMatch.Exact)]
    [InlineData("**", Partners + "/acme", "*.xml", FolderMatch.Exact)]
    [InlineData("*", Partners + "/acme", "*.xml", FolderMatch.Exact)]
    [InlineData("*.*", Partners + "/acme", "*.xml", FolderMatch.Exact)]
    [InlineData("data/in/**", "/data/in/acme/x", "*.xml", FolderMatch.Exact)]
    [InlineData("data/in/**", "/data/out", "*.xml", FolderMatch.None)]
    // A glob without '/' only matches top-level paths, in the router and here alike.
    [InlineData("*.csv", "/data/in", "*.csv", FolderMatch.None)]
    public void MatchFolder_Glob(string glob, string folder, string sourcePattern, FolderMatch expected)
        => Assert.Equal(expected, RouteMatcher.MatchFolder(Rule(glob), "sftp", folder, '/', sourcePattern));

    [Fact]
    public void MatchFolder_ProtocolMismatch_IsNone()
        => Assert.Equal(FolderMatch.None, RouteMatcher.MatchFolder(Rule("**/*", protocol: "ftp"), "sftp", Partners, '/', "*.*"));

    [Fact]
    public void MatchFolder_NoPathCriteria_IsExact()
        => Assert.Equal(FolderMatch.Exact, RouteMatcher.MatchFolder(Rule(protocol: "sftp"), "sftp", Partners, '/', "*.*"));

    [Theory]
    [InlineData("/ORDRSP/", FolderMatch.PatternDependent)]
    [InlineData(@"\.xml$", FolderMatch.PatternDependent)]
    [InlineData("/INVOIC/", FolderMatch.None)]
    public void MatchFolder_Regex_IsNeverExact(string regex, FolderMatch expected)
        => Assert.Equal(expected, RouteMatcher.MatchFolder(Rule(regex: regex), "sftp", Partners + "/acme/ORDRSP", '/', "*.xml"));

    [Fact]
    public void MatchFolder_GlobAndRegex_BothMustHold()
    {
        var rule = Rule("**/ORDRSP/*.xml", regex: "/acme/");
        Assert.Equal(FolderMatch.PatternDependent, RouteMatcher.MatchFolder(rule, "sftp", Partners + "/acme/ORDRSP", '/', "*.xml"));
        Assert.Equal(FolderMatch.None, RouteMatcher.MatchFolder(rule, "sftp", Partners + "/globex/ORDRSP", '/', "*.xml"));
    }

    [Fact]
    public void MatchFolder_LocalWindowsPath()
        => Assert.Equal(FolderMatch.Exact, RouteMatcher.MatchFolder(Rule("**/ORDRSP/*.xml"), "local", @"C:\data\in\acme\ORDRSP", '\\', "*.xml"));

    public static TheoryData<string, string> RouterMatches => new()
    {
        { "**/ORDRSP/*.xml", Partners + "/acme/ORDRSP/1.xml" },
        { "**/ORDRSP/ORD_*.xml", Partners + "/acme/ORDRSP/ORD_1.xml" },
        { "**/*", Partners + "/acme/x.bin" },
        { "data/in/**", "/data/in/acme/x/y.txt" },
        { "**/*.csv", "/data/in/a.csv" },
    };

    /// <summary>
    /// The inventory must never call a folder unresolved when the router would route a file there: every
    /// file the router matches must give its folder a match under the same pattern-agnostic source.
    /// </summary>
    [Theory]
    [MemberData(nameof(RouterMatches))]
    public void MatchFolder_NeverMissesWhatTheRouterMatches(string glob, string file)
    {
        var rule = Rule(glob);
        Assert.True(RouteMatcher.Matches(rule, "sftp", file));

        var folder = file[..file.LastIndexOf('/')];
        Assert.NotEqual(FolderMatch.None, RouteMatcher.MatchFolder(rule, "sftp", folder, '/', "*.*"));
        Assert.NotEqual(FolderMatch.None, RouteMatcher.MatchFolder(rule, "sftp", folder, '/', "*" + Path.GetExtension(file)));
    }
}

public class RemoteFolderCollectorTests
{
    [Fact]
    public void Folders_ReportsLeavesAndFoldersWithFiles_NotIntermediates()
    {
        var c = new RemoteFolderCollector("/partners/", recursive: true);
        c.AddDirectory("/partners/acme");
        c.AddDirectory("/partners/acme/ORDRSP");
        c.AddDirectory("/partners/acme/INVOIC");
        c.AddFile("/partners/acme/INVOIC/1.xml");
        c.AddDirectory("/partners/globex");
        c.AddFile("/partners/globex/readme.xml");
        c.AddDirectory("/partners/globex/ORDRSP");

        Assert.Equal(
            ["/partners/acme/INVOIC", "/partners/acme/ORDRSP", "/partners/globex", "/partners/globex/ORDRSP"],
            c.Folders());
    }

    [Fact]
    public void Folders_EmptyRoot_IsReported()
        => Assert.Equal(["/partners"], new RemoteFolderCollector("/partners", recursive: true).Folders());

    [Fact]
    public void Folders_RootWithFiles_IsReportedFirst()
    {
        var c = new RemoteFolderCollector("/", recursive: true);
        c.AddFile("/a.xml");
        c.AddDirectory("/sub");
        Assert.Equal(["/", "/sub"], c.Folders());
    }

    [Fact]
    public void Folders_NonRecursive_IsRootOnly()
    {
        var c = new RemoteFolderCollector("/partners", recursive: false);
        c.AddDirectory("/partners/acme");
        Assert.Equal(["/partners"], c.Folders());
    }
}

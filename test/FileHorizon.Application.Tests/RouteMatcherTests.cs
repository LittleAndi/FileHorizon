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
    // One extension being a suffix of the other overlaps (every *.tar.gz is a *.gz).
    [InlineData("**/archive/*.gz", Partners + "/archive", "*.tar.gz", FolderMatch.Exact)]
    [InlineData("**/archive/*.tar.gz", Partners + "/archive", "*.gz", FolderMatch.PatternDependent)]
    [InlineData("**/in/*.edi.xml", Partners + "/in", "*.xml", FolderMatch.PatternDependent)]
    [InlineData("**/in/*.xml", Partners + "/in", "*.edi.xml", FolderMatch.Exact)]
    [InlineData("**/in/*.gz", Partners + "/in", "*.zip", FolderMatch.None)]
    // A trailing '/' means everything below the folder, nested folders included.
    [InlineData("**/ORDRSP/", Partners + "/acme/ORDRSP", "*.xml", FolderMatch.Exact)]
    [InlineData("**/ORDRSP/", Partners + "/acme/ORDRSP/sub", "*.xml", FolderMatch.Exact)]
    [InlineData("**/ORDRSP/", Partners + "/acme/INVOIC", "*.xml", FolderMatch.None)]
    public void MatchFolder_Glob(string glob, string folder, string sourcePattern, FolderMatch expected)
        => Assert.Equal(expected, RouteMatcher.MatchFolder(Rule(glob), "sftp", folder, '/', sourcePattern));

    [Fact]
    public void MatchFolder_ProtocolMismatch_IsNone()
        => Assert.Equal(FolderMatch.None, RouteMatcher.MatchFolder(Rule("**/*", protocol: "ftp"), "sftp", Partners, '/', "*.*"));

    [Fact]
    public void MatchFolder_NoPathCriteria_IsExact()
        => Assert.Equal(FolderMatch.Exact, RouteMatcher.MatchFolder(Rule(protocol: "sftp"), "sftp", Partners, '/', "*.*"));

    /// <summary>A regex needs a real file name, so it is always pattern-dependent and never rules a folder out.</summary>
    [Theory]
    [InlineData("/ORDRSP/")]
    [InlineData(@"\.xml$")]
    [InlineData(@"/ORDRSP/\d+\.xml$")]
    [InlineData("/INVOIC/")]
    public void MatchFolder_Regex_IsAlwaysPatternDependent(string regex)
        => Assert.Equal(FolderMatch.PatternDependent, RouteMatcher.MatchFolder(Rule(regex: regex), "sftp", Partners + "/acme/ORDRSP", '/', "*.*"));

    [Fact]
    public void MatchFolder_GlobAndRegex_GlobStillRulesFoldersOut()
    {
        var rule = Rule("**/ORDRSP/*.xml", regex: "/acme/");
        Assert.Equal(FolderMatch.PatternDependent, RouteMatcher.MatchFolder(rule, "sftp", Partners + "/acme/ORDRSP", '/', "*.xml"));
        Assert.Equal(FolderMatch.None, RouteMatcher.MatchFolder(rule, "sftp", Partners + "/acme/INVOIC", '/', "*.xml"));
    }

    [Fact]
    public void MatchFolder_LocalWindowsPath()
        => Assert.Equal(FolderMatch.Exact, RouteMatcher.MatchFolder(Rule("**/ORDRSP/*.xml"), "local", @"C:\data\in\acme\ORDRSP", '\\', "*.xml"));

    public static TheoryData<string?, string?, string> RouterMatches => new()
    {
        { "**/ORDRSP/*.xml", null, Partners + "/acme/ORDRSP/1.xml" },
        { "**/ORDRSP/ORD_*.xml", null, Partners + "/acme/ORDRSP/ORD_1.xml" },
        { "**/*", null, Partners + "/acme/x.bin" },
        { "data/in/**", null, "/data/in/acme/x/y.txt" },
        { "**/*.csv", null, "/data/in/a.csv" },
        { "**/archive/*.gz", null, Partners + "/archive/x.tar.gz" },
        { "**/ORDRSP/", null, Partners + "/acme/ORDRSP/sub/a.xml" },
        { null, @"\.xml$", Partners + "/acme/ORDRSP/1.xml" },
        { null, @"/ORDRSP/\d+\.xml$", Partners + "/acme/ORDRSP/123.xml" },
        { "**/ORDRSP/*", "/acme/", Partners + "/acme/ORDRSP/1.xml" },
    };

    /// <summary>
    /// The inventory must never call a folder unresolved when the router would route a file there: every
    /// file the router matches must give its folder a match under the same pattern-agnostic source.
    /// </summary>
    [Theory]
    [MemberData(nameof(RouterMatches))]
    public void MatchFolder_NeverMissesWhatTheRouterMatches(string? glob, string? regex, string file)
    {
        var rule = Rule(glob, regex);
        Assert.True(RouteMatcher.Matches(rule, "sftp", file));

        var folder = file[..file.LastIndexOf('/')];
        Assert.NotEqual(FolderMatch.None, RouteMatcher.MatchFolder(rule, "sftp", folder, '/', "*.*"));
        Assert.NotEqual(FolderMatch.None, RouteMatcher.MatchFolder(rule, "sftp", folder, '/', "*" + Path.GetExtension(file)));
        var fullExtension = Path.GetFileName(file)[Path.GetFileName(file).IndexOf('.')..];
        Assert.NotEqual(FolderMatch.None, RouteMatcher.MatchFolder(rule, "sftp", folder, '/', "*" + fullExtension));
    }
}

public class FolderCollectorTests
{
    [Fact]
    public void Folders_ReportsLeavesAndFoldersWithFiles_NotIntermediates()
    {
        var c = new FolderCollector("/partners/", '/', recursive: true);
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
        => Assert.Equal(["/partners"], new FolderCollector("/partners", '/', recursive: true).Folders());

    [Fact]
    public void Folders_RootWithFiles_IsReportedFirst()
    {
        var c = new FolderCollector("/", '/', recursive: true);
        c.AddFile("/a.xml");
        c.AddDirectory("/sub");
        Assert.Equal(["/", "/sub"], c.Folders());
    }

    [Fact]
    public void Folders_NonRecursive_IsRootOnly()
    {
        var c = new FolderCollector("/partners", '/', recursive: false);
        c.AddDirectory("/partners/acme");
        Assert.Equal(["/partners"], c.Folders());
    }

    /// <summary>SSH.NET lists the canonical path, so a symlinked root comes back under its target.</summary>
    [Fact]
    public void Folders_RootReportedUnderACanonicalPath_IsAdopted()
    {
        var c = new FolderCollector("/inbound", '/', recursive: true);
        c.AddDirectory("/data/inbound/acme");
        c.AddDirectory("/data/inbound/acme/ORDRSP");
        c.AddFile("/data/inbound/acme/ORDRSP/1.xml");
        Assert.Equal(["/data/inbound/acme/ORDRSP"], c.Folders());
    }

    [Fact]
    public void Folders_RepeatedSlashesAreCollapsed()
    {
        var c = new FolderCollector("/a//b/", '/', recursive: true);
        c.AddDirectory("/a/b/c");
        Assert.Equal(["/a/b/c"], c.Folders());
    }

    [Fact]
    public void Folders_WindowsSeparator()
    {
        var c = new FolderCollector(@"C:\in", '\\', recursive: true);
        c.AddDirectory(@"C:\in\acme");
        c.AddDirectory(@"C:\in\acme\ORDRSP");
        c.AddFile(@"C:\in\acme\notes.xml");
        Assert.Equal([@"C:\in\acme", @"C:\in\acme\ORDRSP"], c.Folders());
    }
}

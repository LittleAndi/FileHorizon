using FileHorizon.Application.Common;
using FileHorizon.Application.Models;

namespace FileHorizon.Application.Tests;

public class FileIdentityKeyTests
{
    private static FileMetadata Metadata(string path, long size, DateTimeOffset mtime)
        => new(path, size, mtime, "none", null);

    [Fact]
    public void BuildIdempotencyKey_LocalPath_HasExpectedFormat()
    {
        var mtime = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var key = FileIdentity.BuildIdempotencyKey(Metadata(@"C:\inbox\a.txt", 42, mtime));

        Assert.Equal($"fh:idemp:v2:C:\\inbox\\a.txt|42|{mtime:O}", key);
    }

    [Fact]
    public void BuildIdempotencyKey_RemotePath_HasExpectedFormat()
    {
        var mtime = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var key = FileIdentity.BuildIdempotencyKey(Metadata("sftp://host:22/inbox/a.txt", 42, mtime));

        Assert.StartsWith("fh:idemp:v2:sftp://host:22/inbox/a.txt|42|", key);
    }

    [Fact]
    public void BuildIdempotencyKey_SameInstantDifferentOffsets_YieldSameKey()
    {
        var utc = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var plusTwo = utc.ToOffset(TimeSpan.FromHours(2));

        var keyUtc = FileIdentity.BuildIdempotencyKey(Metadata("/inbox/a.txt", 42, utc));
        var keyPlusTwo = FileIdentity.BuildIdempotencyKey(Metadata("/inbox/a.txt", 42, plusTwo));

        Assert.Equal(keyUtc, keyPlusTwo);
    }

    [Fact]
    public void BuildIdempotencyKey_SizeChange_YieldsDifferentKey()
    {
        var mtime = DateTimeOffset.UtcNow;
        var a = FileIdentity.BuildIdempotencyKey(Metadata("/inbox/a.txt", 42, mtime));
        var b = FileIdentity.BuildIdempotencyKey(Metadata("/inbox/a.txt", 43, mtime));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void BuildIdempotencyKey_MtimeChange_YieldsDifferentKey()
    {
        var mtime = DateTimeOffset.UtcNow;
        var a = FileIdentity.BuildIdempotencyKey(Metadata("/inbox/a.txt", 42, mtime));
        var b = FileIdentity.BuildIdempotencyKey(Metadata("/inbox/a.txt", 42, mtime.AddSeconds(1)));

        Assert.NotEqual(a, b);
    }
}

using Nbn.Demos.Basics.Environment;

namespace Nbn.Demos.Basics.Environment.Tests;

public sealed class BasicsRunLogRetentionPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "nbn-basics-run-log-retention",
        Guid.NewGuid().ToString("N"));

    public BasicsRunLogRetentionPolicyTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Apply_DeletesOldestUnmarkedLogs_WhenFileCountExceedsLimit()
    {
        var now = new DateTimeOffset(2026, 4, 13, 12, 0, 0, TimeSpan.Zero);
        var oldest = CreateLog("basics-ui-multiplication-20260413-010000.jsonl", 64, now.AddHours(-4));
        var middle = CreateLog("basics-ui-multiplication-20260413-020000.jsonl", 64, now.AddHours(-3));
        var newest = CreateLog("basics-ui-multiplication-20260413-030000.jsonl", 64, now.AddHours(-2));
        var preserved = CreateLog("basics-ui-multiplication-20260413-040000.jsonl", 64, now.AddHours(-5));
        File.WriteAllText(preserved + BasicsRunLogRetentionOptions.DefaultKeepMarkerSuffix, "keep");

        var result = BasicsRunLogRetentionPolicy.Apply(
            _root,
            now,
            new BasicsRunLogRetentionOptions
            {
                MaxFiles = 2,
                MaxTotalBytes = 1024,
                MaxAgeDays = 30
            });

        Assert.True(result.Enabled);
        Assert.Equal(1, result.DeletedFileCount);
        Assert.Equal(1, result.PreservedFileCount);
        Assert.False(File.Exists(oldest));
        Assert.True(File.Exists(middle));
        Assert.True(File.Exists(newest));
        Assert.True(File.Exists(preserved));
    }

    [Fact]
    public void Apply_PreservesMarkedLogs_WhenTotalSizeExceedsLimit()
    {
        var now = new DateTimeOffset(2026, 4, 13, 12, 0, 0, TimeSpan.Zero);
        var preserved = CreateLog("basics-ui-multiplication-20260413-010000.jsonl", 600, now.AddHours(-4));
        var oldUnmarked = CreateLog("basics-ui-multiplication-20260413-020000.jsonl", 600, now.AddHours(-3));
        var newUnmarked = CreateLog("basics-ui-multiplication-20260413-030000.jsonl", 600, now.AddHours(-2));
        File.WriteAllText(preserved + BasicsRunLogRetentionOptions.DefaultKeepMarkerSuffix, "keep");

        var result = BasicsRunLogRetentionPolicy.Apply(
            _root,
            now,
            new BasicsRunLogRetentionOptions
            {
                MaxFiles = 10,
                MaxTotalBytes = 900,
                MaxAgeDays = 30
            });

        Assert.Equal(1, result.DeletedFileCount);
        Assert.True(File.Exists(preserved));
        Assert.False(File.Exists(oldUnmarked));
        Assert.True(File.Exists(newUnmarked));
    }

    [Fact]
    public void Apply_DeletesLogsOlderThanAgeLimit()
    {
        var now = new DateTimeOffset(2026, 4, 13, 12, 0, 0, TimeSpan.Zero);
        var stale = CreateLog("basics-ui-multiplication-20260401-010000.jsonl", 64, now.AddDays(-10));
        var fresh = CreateLog("basics-ui-multiplication-20260413-010000.jsonl", 64, now.AddHours(-1));

        var result = BasicsRunLogRetentionPolicy.Apply(
            _root,
            now,
            new BasicsRunLogRetentionOptions
            {
                MaxFiles = 10,
                MaxTotalBytes = 1024,
                MaxAgeDays = 7
            });

        Assert.Equal(1, result.DeletedFileCount);
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void Apply_DoesNothing_WhenRetentionDisabled()
    {
        var now = new DateTimeOffset(2026, 4, 13, 12, 0, 0, TimeSpan.Zero);
        var oldest = CreateLog("basics-ui-multiplication-20260413-010000.jsonl", 64, now.AddHours(-4));
        var newest = CreateLog("basics-ui-multiplication-20260413-020000.jsonl", 64, now.AddHours(-3));

        var result = BasicsRunLogRetentionPolicy.Apply(
            _root,
            now,
            new BasicsRunLogRetentionOptions
            {
                Enabled = false,
                MaxFiles = 1,
                MaxTotalBytes = 1,
                MaxAgeDays = 1
            });

        Assert.False(result.Enabled);
        Assert.Equal(0, result.DeletedFileCount);
        Assert.True(File.Exists(oldest));
        Assert.True(File.Exists(newest));
    }

    [Fact]
    public void Apply_UsesCustomKeepMarkerSuffix()
    {
        var now = new DateTimeOffset(2026, 4, 13, 12, 0, 0, TimeSpan.Zero);
        var preserved = CreateLog("basics-ui-multiplication-20260413-010000.jsonl", 600, now.AddHours(-4));
        var unmarked = CreateLog("basics-ui-multiplication-20260413-020000.jsonl", 800, now.AddHours(-3));
        File.WriteAllText(preserved + ".save", "keep");

        var result = BasicsRunLogRetentionPolicy.Apply(
            _root,
            now,
            new BasicsRunLogRetentionOptions
            {
                MaxFiles = 10,
                MaxTotalBytes = 700,
                MaxAgeDays = 30,
                KeepMarkerSuffix = ".save"
            });

        Assert.Equal(1, result.PreservedFileCount);
        Assert.Equal(1, result.DeletedFileCount);
        Assert.True(File.Exists(preserved));
        Assert.False(File.Exists(unmarked));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    private string CreateLog(string fileName, int byteCount, DateTimeOffset lastWriteUtc)
    {
        var path = Path.Combine(_root, fileName);
        File.WriteAllBytes(path, Enumerable.Repeat((byte)'x', byteCount).ToArray());
        File.SetLastWriteTimeUtc(path, lastWriteUtc.UtcDateTime);
        return path;
    }
}

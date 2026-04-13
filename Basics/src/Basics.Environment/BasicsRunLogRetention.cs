namespace Nbn.Demos.Basics.Environment;

public sealed record BasicsRunLogRetentionOptions
{
    public const long Mebibyte = 1024L * 1024L;
    public const long DefaultMaxFileBytes = 256L * Mebibyte;
    public const long DefaultMaxTotalBytes = 4L * 1024L * Mebibyte;
    public const int DefaultMaxFiles = 32;
    public const int DefaultMaxAgeDays = 14;
    public const string DefaultKeepMarkerSuffix = ".keep";

    public bool Enabled { get; init; } = true;
    public long MaxFileBytes { get; init; } = DefaultMaxFileBytes;
    public long MaxTotalBytes { get; init; } = DefaultMaxTotalBytes;
    public int MaxFiles { get; init; } = DefaultMaxFiles;
    public int MaxAgeDays { get; init; } = DefaultMaxAgeDays;
    public string KeepMarkerSuffix { get; init; } = DefaultKeepMarkerSuffix;

    public static BasicsRunLogRetentionOptions Default { get; } = new();

    public BasicsRunLogRetentionOptions Normalize()
        => this with
        {
            MaxFileBytes = Math.Max(1L, MaxFileBytes),
            MaxTotalBytes = Math.Max(1L, MaxTotalBytes),
            MaxFiles = Math.Max(1, MaxFiles),
            MaxAgeDays = Math.Max(1, MaxAgeDays),
            KeepMarkerSuffix = string.IsNullOrWhiteSpace(KeepMarkerSuffix)
                ? DefaultKeepMarkerSuffix
                : KeepMarkerSuffix.Trim()
        };
}

public sealed record BasicsRunLogRetentionResult(
    bool Enabled,
    int CandidateFileCount,
    int PreservedFileCount,
    int DeletedFileCount,
    int FailedDeleteCount,
    long TotalBytesBefore,
    long TotalBytesAfter,
    long DeletedBytes,
    IReadOnlyList<string> DeletedFiles,
    IReadOnlyList<string> FailedDeletes);

public static class BasicsRunLogRetentionPolicy
{
    private const string RunLogSearchPattern = "basics-ui-*.jsonl";

    public static BasicsRunLogRetentionOptions FromEnvironment()
    {
        var defaults = BasicsRunLogRetentionOptions.Default;
        return new BasicsRunLogRetentionOptions
        {
            Enabled = ReadBoolean("NBN_BASICS_UI_RUN_LOG_RETENTION_ENABLED", defaults.Enabled),
            MaxFileBytes = ReadMebibytes("NBN_BASICS_UI_RUN_LOG_MAX_FILE_MB", defaults.MaxFileBytes),
            MaxTotalBytes = ReadMebibytes("NBN_BASICS_UI_RUN_LOG_MAX_TOTAL_MB", defaults.MaxTotalBytes),
            MaxFiles = ReadInt("NBN_BASICS_UI_RUN_LOG_MAX_FILES", defaults.MaxFiles),
            MaxAgeDays = ReadInt("NBN_BASICS_UI_RUN_LOG_MAX_AGE_DAYS", defaults.MaxAgeDays),
            KeepMarkerSuffix = ReadString("NBN_BASICS_UI_RUN_LOG_KEEP_MARKER_SUFFIX", defaults.KeepMarkerSuffix)
        }.Normalize();
    }

    public static BasicsRunLogRetentionResult Apply(
        string directory,
        DateTimeOffset nowUtc,
        BasicsRunLogRetentionOptions? options = null,
        IReadOnlySet<string>? protectedPaths = null)
    {
        var normalized = (options ?? BasicsRunLogRetentionOptions.Default).Normalize();
        if (!normalized.Enabled || !Directory.Exists(directory))
        {
            return new BasicsRunLogRetentionResult(
                normalized.Enabled,
                CandidateFileCount: 0,
                PreservedFileCount: 0,
                DeletedFileCount: 0,
                FailedDeleteCount: 0,
                TotalBytesBefore: 0,
                TotalBytesAfter: 0,
                DeletedBytes: 0,
                DeletedFiles: Array.Empty<string>(),
                FailedDeletes: Array.Empty<string>());
        }

        var files = Directory
            .EnumerateFiles(directory, RunLogSearchPattern, SearchOption.TopDirectoryOnly)
            .Select(static path => new FileInfo(path))
            .Where(static file => file.Exists)
            .Select(file => new RunLogFile(
                file.FullName,
                file.Name,
                file.Length,
                new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero)))
            .ToArray();
        var totalBefore = files.Sum(static file => file.SizeBytes);

        var normalizedProtectedPaths = new HashSet<string>(StringComparer.Ordinal);
        if (protectedPaths is not null)
        {
            foreach (var path in protectedPaths)
            {
                normalizedProtectedPaths.Add(Path.GetFullPath(path));
            }
        }

        var preserved = files
            .Where(file => IsPreserved(file.Path, normalized.KeepMarkerSuffix, normalizedProtectedPaths))
            .ToArray();
        var candidates = files
            .Where(file => !IsPreserved(file.Path, normalized.KeepMarkerSuffix, normalizedProtectedPaths))
            .OrderBy(static file => file.LastWriteUtc)
            .ThenBy(static file => file.Name, StringComparer.Ordinal)
            .ToList();
        var deleteSet = new HashSet<string>(StringComparer.Ordinal);
        var cutoff = nowUtc.ToUniversalTime().AddDays(-normalized.MaxAgeDays);
        foreach (var file in candidates)
        {
            if (file.LastWriteUtc < cutoff)
            {
                deleteSet.Add(file.Path);
            }
        }

        AddOldestUntilWithinCount(candidates, deleteSet, normalized.MaxFiles);
        AddOldestUntilWithinTotal(candidates, deleteSet, normalized.MaxTotalBytes);

        var deletedFiles = new List<string>();
        var failedDeletes = new List<string>();
        long deletedBytes = 0;
        foreach (var file in candidates.Where(file => deleteSet.Contains(file.Path)))
        {
            try
            {
                File.Delete(file.Path);
                deletedFiles.Add(file.Name);
                deletedBytes += file.SizeBytes;
            }
            catch (Exception ex)
            {
                failedDeletes.Add($"{file.Name}: {ex.GetBaseException().Message}");
            }
        }

        var totalAfter = totalBefore - deletedBytes;
        return new BasicsRunLogRetentionResult(
            Enabled: true,
            CandidateFileCount: candidates.Count,
            PreservedFileCount: preserved.Length,
            DeletedFileCount: deletedFiles.Count,
            FailedDeleteCount: failedDeletes.Count,
            TotalBytesBefore: totalBefore,
            TotalBytesAfter: totalAfter,
            DeletedBytes: deletedBytes,
            DeletedFiles: deletedFiles.ToArray(),
            FailedDeletes: failedDeletes.ToArray());
    }

    private static void AddOldestUntilWithinCount(
        IReadOnlyList<RunLogFile> orderedCandidates,
        HashSet<string> deleteSet,
        int maxFiles)
    {
        var remainingCount = orderedCandidates.Count(file => !deleteSet.Contains(file.Path));
        foreach (var file in orderedCandidates)
        {
            if (remainingCount <= maxFiles)
            {
                break;
            }

            if (deleteSet.Add(file.Path))
            {
                remainingCount--;
            }
        }
    }

    private static void AddOldestUntilWithinTotal(
        IReadOnlyList<RunLogFile> orderedCandidates,
        HashSet<string> deleteSet,
        long maxTotalBytes)
    {
        var remainingBytes = orderedCandidates
            .Where(file => !deleteSet.Contains(file.Path))
            .Sum(static file => file.SizeBytes);
        foreach (var file in orderedCandidates)
        {
            if (remainingBytes <= maxTotalBytes)
            {
                break;
            }

            if (deleteSet.Add(file.Path))
            {
                remainingBytes -= file.SizeBytes;
            }
        }
    }

    private static bool HasKeepMarker(string logPath, string keepMarkerSuffix)
        => File.Exists(logPath + keepMarkerSuffix);

    private static bool IsPreserved(
        string logPath,
        string keepMarkerSuffix,
        HashSet<string> normalizedProtectedPaths)
        => normalizedProtectedPaths.Contains(Path.GetFullPath(logPath))
           || HasKeepMarker(logPath, keepMarkerSuffix);

    private static bool ReadBoolean(string key, bool fallback)
    {
        var value = System.Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => fallback
        };
    }

    private static long ReadMebibytes(string key, long fallbackBytes)
    {
        var value = System.Environment.GetEnvironmentVariable(key);
        return long.TryParse(value, out var mebibytes)
               && mebibytes > 0
               && mebibytes <= long.MaxValue / BasicsRunLogRetentionOptions.Mebibyte
            ? mebibytes * BasicsRunLogRetentionOptions.Mebibyte
            : fallbackBytes;
    }

    private static int ReadInt(string key, int fallback)
    {
        var value = System.Environment.GetEnvironmentVariable(key);
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
    }

    private static string ReadString(string key, string fallback)
    {
        var value = System.Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private sealed record RunLogFile(string Path, string Name, long SizeBytes, DateTimeOffset LastWriteUtc);
}

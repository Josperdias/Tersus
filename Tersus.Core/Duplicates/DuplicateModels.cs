namespace Tersus.Core.Duplicates;

public sealed record DuplicateOptions
{
    /// <summary>Files smaller than this are ignored (tiny files are rarely worth deduplicating and are very numerous).</summary>
    public long MinFileBytes { get; init; } = 1L << 20;

    /// <summary>At most this many files enter the comparison; if there are more, the LARGEST ones are kept and the result says so.</summary>
    public int MaxInventoryFiles { get; init; } = 80_000;

    /// <summary>Full-content SHA-256 is computed for at most this many files...</summary>
    public int MaxFullyHashedFiles { get; init; } = 3_000;

    /// <summary>...and for at most this many bytes read in total. Beyond that, groups are reported as "not verified".</summary>
    public long MaxFullyHashedBytes { get; init; } = 20L << 30;

    /// <summary>Bytes read from the start and from the end of each candidate for the cheap first comparison.</summary>
    public int SampleBytes { get; init; } = 64 * 1024;

    public int ProgressIntervalMs { get; init; } = 200;
}

public sealed record DuplicateFile(string Path, DateTime LastWriteUtc, bool InSensitiveLocation);

/// <summary>
/// Files with the same size and (when <see cref="Verified"/>) the same SHA-256. Purely a report: Tersus never deletes duplicates.
/// <see cref="PotentialBytes"/> is a potential, not space that was freed.
/// </summary>
public sealed record DuplicateGroup(long Size, string? Sha256, IReadOnlyList<DuplicateFile> Files, bool Verified)
{
    public long PotentialBytes => Size * Math.Max(0, Files.Count - 1);
}

public sealed record DuplicateStats(
    long InventoryFiles,
    long ConsideredFiles,
    long SampledFiles,
    long FullyHashedFiles,
    long BytesRead,
    long UnreadableFiles,
    long ChangedWhileReading,
    long HardLinksIgnored,
    bool InventoryTruncated,
    bool HashBudgetExhausted);

public sealed record DuplicateResult(
    IReadOnlyList<DuplicateGroup> Groups,
    DuplicateStats Stats,
    bool Completed,
    IReadOnlyList<string> Warnings)
{
    public long VerifiedPotentialBytes => Groups.Where(g => g.Verified).Sum(g => g.PotentialBytes);

    public long UnverifiedPotentialBytes => Groups.Where(g => !g.Verified).Sum(g => g.PotentialBytes);
}

public readonly record struct DuplicateProgress(string Stage, long Done, long Total, string? Current);

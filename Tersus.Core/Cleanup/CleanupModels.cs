using Tersus.Core.IO;
using Tersus.Core.Policy;

namespace Tersus.Core.Cleanup;

/// <summary>A temporary file that passed every policy check at analysis time.</summary>
public sealed record CleanupCandidate(
    string Path,
    string RelativePath,
    long Length,
    DateTime CreatedUtc,
    DateTime ModifiedUtc,
    int AgeDays,
    FileIdentity? Identity)
{
    public string Name => PathGuard.FileNameOf(Path);

    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
}

public sealed record CandidateStats(
    int TempFilesSeen,
    long OtherFilesSeen,
    int Eligible,
    long EligibleBytes,
    IReadOnlyDictionary<RefusalReason, int> Refused,
    long LinksSkipped,
    long InaccessibleFolders,
    bool Truncated);

public sealed record CandidateScanResult(
    IReadOnlyList<CleanupCandidate> Candidates,
    CandidateStats Stats,
    string TempRoot,
    bool Completed);

public readonly record struct CandidateScanProgress(long FilesVisited, int Eligible, string? CurrentFolder);

public sealed record PlanItem(CleanupCandidate Candidate, PolicyDecision Decision, FileFacts Facts)
{
    public bool Approved => Decision.Eligible;
}

/// <summary>
/// The result of a simulation: exactly which files WOULD be sent to the Recycle Bin and which were refused (and why).
/// Building a plan never changes anything on disk.
/// </summary>
public sealed class CleanupPlan
{
    public CleanupPlan(IReadOnlyList<PlanItem> items, DateTime createdUtc)
    {
        Items = items;
        CreatedUtc = createdUtc;
        Id = Guid.NewGuid();
        Approved = [.. items.Where(i => i.Approved)];
        Refused = [.. items.Where(i => !i.Approved)];
        ApprovedBytes = Approved.Sum(i => i.Candidate.Length);
        RefusedByReason = Refused
            .GroupBy(i => i.Decision.Reason)
            .ToDictionary(g => g.Key, g => g.Count());
        Fingerprint = ComputeFingerprint(Approved);
    }

    public Guid Id { get; }

    public DateTime CreatedUtc { get; }

    public IReadOnlyList<PlanItem> Items { get; }

    public IReadOnlyList<PlanItem> Approved { get; }

    public IReadOnlyList<PlanItem> Refused { get; }

    public long ApprovedBytes { get; }

    public IReadOnlyDictionary<RefusalReason, int> RefusedByReason { get; }

    /// <summary>Hash of exactly what the user is being asked to confirm; execution only proceeds for the same plan.</summary>
    public string Fingerprint { get; }

    private static string ComputeFingerprint(IReadOnlyList<PlanItem> approved)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        foreach (PlanItem i in approved.OrderBy(i => i.Candidate.Path, StringComparer.OrdinalIgnoreCase))
        {
            byte[] line = System.Text.Encoding.UTF8.GetBytes($"{i.Candidate.Path.ToUpperInvariant()}|{i.Candidate.Length}|{i.Candidate.ModifiedUtc.Ticks}|{i.Candidate.Identity}\n");
            sha.TransformBlock(line, 0, line.Length, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }
}

/// <summary>Proof that the user confirmed one specific plan. Execution refuses anything else.</summary>
public sealed record ExecutionConfirmation(Guid PlanId, string PlanFingerprint, DateTime ConfirmedUtc)
{
    public static ExecutionConfirmation For(CleanupPlan plan, DateTime nowUtc) => new(plan.Id, plan.Fingerprint, nowUtc);

    public bool Matches(CleanupPlan plan) => PlanId == plan.Id && string.Equals(PlanFingerprint, plan.Fingerprint, StringComparison.Ordinal);
}

public sealed record CleanupSelection(IReadOnlyList<CleanupCandidate> Items, long TotalBytes, bool GoalReached, long ShortfallBytes, long AvailableBytes);

public enum CleanupItemOutcome
{
    MovedToRecycleBin,
    Skipped,
    Failed,
    UnprovenPossiblePermanentDeletion,
}

public sealed record CleanupItemResult(string Path, long Length, CleanupItemOutcome Outcome, RefusalReason Reason, string Message);

public sealed record CleanupReport(
    CleanupPlan Plan,
    IReadOnlyList<CleanupItemResult> Items,
    DateTime StartedUtc,
    TimeSpan Duration,
    bool Aborted,
    string? AbortReason,
    string PreflightMessage,
    long? FreeBytesBefore,
    long? FreeBytesAfter,
    string? LogPath)
{
    public int MovedCount => Items.Count(i => i.Outcome == CleanupItemOutcome.MovedToRecycleBin);

    /// <summary>Logical size of what went to the Recycle Bin. NOT the same as space freed: that happens only when the user empties the bin.</summary>
    public long MovedBytes => Items.Where(i => i.Outcome == CleanupItemOutcome.MovedToRecycleBin).Sum(i => i.Length);

    public int SkippedCount => Items.Count(i => i.Outcome == CleanupItemOutcome.Skipped);

    public int FailedCount => Items.Count(i => i.Outcome is CleanupItemOutcome.Failed or CleanupItemOutcome.UnprovenPossiblePermanentDeletion);

    public bool HasUnprovenDeletion => Items.Any(i => i.Outcome == CleanupItemOutcome.UnprovenPossiblePermanentDeletion);

    /// <summary>Free-space change actually measured on the volume (usually about zero right after moving to the Recycle Bin).</summary>
    public long? MeasuredFreeSpaceDelta => FreeBytesBefore is not null && FreeBytesAfter is not null ? FreeBytesAfter - FreeBytesBefore : null;
}

public readonly record struct ExecutionProgress(int Done, int Total, string? CurrentPath);

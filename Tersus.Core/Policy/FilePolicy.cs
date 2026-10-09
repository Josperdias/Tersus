using Tersus.Core.IO;

namespace Tersus.Core.Policy;

/// <summary>
/// THE gate for destructive actions. A file may be sent to the Recycle Bin only if every rule below holds:
/// <list type="bullet">
/// <item>a plain absolute drive path, strictly inside the current user's TEMP folder;</item>
/// <item>extension exactly ".tmp" or ".temp";</item>
/// <item>a regular file: not a folder, not a link/junction/cloud placeholder, not system/read-only;</item>
/// <item>created AND modified at least 14 days ago, no timestamp in the future;</item>
/// <item>at most 256 MiB;</item>
/// <item>(deep check) reached directly: the path resolved through the open handle equals the path given,
/// and the file has a single hard link.</item>
/// </list>
/// Limits can be made stricter by the caller but never looser: the constructor rejects anything wider than
/// the hard-coded floor/ceiling. There is deliberately no switch, setting or environment variable that widens them.
/// </summary>
public sealed class FilePolicy
{
    public const long HardMaxFileBytes = 256L * 1024 * 1024;

    public static readonly TimeSpan HardMinAge = TimeSpan.FromDays(14);

    private static readonly string[] AllowedExtensions = [".tmp", ".temp"];
    private static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);
    private static readonly DateTime EarliestPlausibleUtc = new(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // FILE_ATTRIBUTE_RECALL_ON_OPEN / RECALL_ON_DATA_ACCESS (cloud files that are not really on disk).
    private const uint RecallOnOpen = 0x00040000;
    private const uint RecallOnDataAccess = 0x00400000;

    private readonly IClock _clock;

    public FilePolicy(string tempRoot, IClock? clock = null, TimeSpan? minAge = null, long? maxFileBytes = null)
    {
        if (!PathGuard.TryNormalize(tempRoot, out string root, out PathProblem problem))
        {
            throw new ArgumentException($"Unsafe TEMP root: {problem}", nameof(tempRoot));
        }

        if (PathGuard.Depth(root) < 2)
        {
            throw new ArgumentException("TEMP root is too shallow to be a per-user folder.", nameof(tempRoot));
        }

        MinAge = minAge ?? HardMinAge;
        if (MinAge < HardMinAge)
        {
            throw new ArgumentOutOfRangeException(nameof(minAge), "The minimum age can only be made stricter than 14 days.");
        }

        MaxFileBytes = maxFileBytes ?? HardMaxFileBytes;
        if (MaxFileBytes <= 0 || MaxFileBytes > HardMaxFileBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFileBytes), "The size limit can only be made stricter than 256 MiB.");
        }

        TempRoot = root;
        _clock = clock ?? SystemClock.Instance;
    }

    /// <summary>Canonical, normalised TEMP folder of the current user (no trailing separator).</summary>
    public string TempRoot { get; }

    public TimeSpan MinAge { get; }

    public long MaxFileBytes { get; }

    public DateTime UtcNow => _clock.UtcNow;

    public static bool HasAllowedExtension(string fileName)
    {
        string ext = PathGuard.ExtensionOf(fileName);
        foreach (string allowed in AllowedExtensions)
        {
            if (string.Equals(ext, allowed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Rules that need no file-system access: path syntax, location and extension.</summary>
    public PolicyDecision EvaluateName(string? path)
    {
        if (!PathGuard.TryNormalize(path, out string normalized, out PathProblem problem))
        {
            return PolicyDecision.Refuse(RefusalReason.InvalidPath, PathGuard.Describe(problem));
        }

        if (!PathGuard.IsStrictlyUnder(TempRoot, normalized))
        {
            return PolicyDecision.Refuse(RefusalReason.OutsideTemp);
        }

        if (!HasAllowedExtension(PathGuard.FileNameOf(normalized)))
        {
            return PolicyDecision.Refuse(RefusalReason.WrongExtension);
        }

        return PolicyDecision.Allow(normalized);
    }

    /// <summary>
    /// Full evaluation. With <paramref name="requireDeepFacts"/> (always true before anything is moved) the facts must
    /// come from an open handle so that links, short names and hard links cannot hide the real target.
    /// </summary>
    public PolicyDecision Evaluate(FileFacts facts, bool requireDeepFacts = true)
    {
        ArgumentNullException.ThrowIfNull(facts);

        PolicyDecision named = EvaluateName(facts.Path);
        if (!named.Eligible)
        {
            return named;
        }

        string normalized = named.NormalizedPath!;

        if (!facts.Exists)
        {
            return facts.Error switch
            {
                ProbeError.AccessDenied => PolicyDecision.Refuse(RefusalReason.AccessDenied),
                ProbeError.SharingViolation => PolicyDecision.Refuse(RefusalReason.InUse),
                ProbeError.PathTooLong => PolicyDecision.Refuse(RefusalReason.InvalidPath, PathGuard.Describe(PathProblem.TooLong)),
                ProbeError.Other => PolicyDecision.Refuse(RefusalReason.ProbeFailed),
                _ => PolicyDecision.Refuse(RefusalReason.NotFound),
            };
        }

        // Trust neither flag alone: a directory attribute without IsDirectory is still a directory.
        if (facts.IsDirectory || (facts.Attributes & FileAttributes.Directory) != 0)
        {
            return PolicyDecision.Refuse(RefusalReason.IsDirectory);
        }

        if (facts.IsReparsePoint)
        {
            return PolicyDecision.Refuse(RefusalReason.ReparsePoint);
        }

        // Age: both timestamps must be old. Future or implausible timestamps make the age unknowable.
        DateTime now = _clock.UtcNow;
        if (facts.CreationTimeUtc < EarliestPlausibleUtc || facts.LastWriteTimeUtc < EarliestPlausibleUtc
            || facts.CreationTimeUtc > now + FutureTolerance || facts.LastWriteTimeUtc > now + FutureTolerance)
        {
            return PolicyDecision.Refuse(RefusalReason.TimestampInvalid);
        }

        if (now - facts.CreationTimeUtc < MinAge || now - facts.LastWriteTimeUtc < MinAge)
        {
            return PolicyDecision.Refuse(RefusalReason.TooNew, $"Criado ou modificado há menos de {MinAge.TotalDays:0} dias.");
        }

        if (facts.Length < 0 || facts.Length > MaxFileBytes)
        {
            return PolicyDecision.Refuse(RefusalReason.TooLarge, $"Maior que {SizeText.Format(MaxFileBytes)} (limite de segurança por arquivo).");
        }

        FileAttributes attrs = facts.Attributes;
        uint raw = (uint)attrs;
        if ((attrs & FileAttributes.Offline) != 0 || (raw & (RecallOnOpen | RecallOnDataAccess)) != 0)
        {
            return PolicyDecision.Refuse(RefusalReason.CloudPlaceholder);
        }

        if ((attrs & (FileAttributes.System | FileAttributes.Device)) != 0)
        {
            return PolicyDecision.Refuse(RefusalReason.SystemFile);
        }

        if ((attrs & FileAttributes.ReadOnly) != 0)
        {
            return PolicyDecision.Refuse(RefusalReason.ReadOnly);
        }

        if (requireDeepFacts)
        {
            if (!facts.IsDeep || facts.FinalPath is null || facts.Identity is null || facts.HardLinkCount is null)
            {
                return PolicyDecision.Refuse(RefusalReason.ProbeFailed);
            }

            if (!PathGuard.TryNormalize(PathGuard.StripExtendedPrefix(facts.FinalPath), out string finalPath, out _)
                || !PathGuard.AreSame(finalPath, normalized))
            {
                return PolicyDecision.Refuse(RefusalReason.IndirectPath);
            }

            if (!PathGuard.IsStrictlyUnder(TempRoot, finalPath))
            {
                return PolicyDecision.Refuse(RefusalReason.OutsideTemp);
            }

            // Exactly one name, or no deal: more than one is a hard link (the data is shared with another path),
            // zero means the file is already delete-pending and its state cannot be trusted.
            if (facts.HardLinkCount.Value > 1)
            {
                return PolicyDecision.Refuse(RefusalReason.HardLinked);
            }

            if (facts.HardLinkCount.Value != 1)
            {
                return PolicyDecision.Refuse(RefusalReason.ProbeFailed);
            }
        }

        return PolicyDecision.Allow(normalized);
    }

    /// <summary>Age in whole days for display (never negative).</summary>
    public int AgeDays(FileFacts facts)
    {
        DateTime newest = facts.CreationTimeUtc > facts.LastWriteTimeUtc ? facts.CreationTimeUtc : facts.LastWriteTimeUtc;
        return (int)Math.Max(0, Math.Floor((_clock.UtcNow - newest).TotalDays));
    }
}

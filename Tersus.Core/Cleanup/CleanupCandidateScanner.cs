using Tersus.Core.IO;
using Tersus.Core.Policy;

namespace Tersus.Core.Cleanup;

/// <summary>
/// Looks for the only kind of file Tersus may ever send to the Recycle Bin: old ".tmp"/".temp" files inside the user's TEMP folder.
/// Read-only. Folders that are links are not entered; folders that cannot be read are counted and skipped.
/// Cheap checks (extension, age, size, attributes) run on the directory listing; only survivors are opened for the deep check.
/// </summary>
public sealed class CleanupCandidateScanner(FilePolicy policy, IFileSystem fs)
{
    public CandidateScanResult Scan(
        IProgress<CandidateScanProgress>? progress = null,
        CancellationToken cancellationToken = default,
        int maxCandidates = 50_000,
        string? startFolder = null)
    {
        string start = policy.TempRoot;
        if (startFolder is not null)
        {
            if (!PathGuard.TryNormalize(startFolder, out string normalizedStart, out _)
                || !(PathGuard.AreSame(normalizedStart, policy.TempRoot) || PathGuard.IsStrictlyUnder(policy.TempRoot, normalizedStart)))
            {
                throw new ArgumentException("The start folder must be the TEMP folder or a folder inside it.", nameof(startFolder));
            }

            start = normalizedStart;
        }

        var candidates = new List<CleanupCandidate>();
        var refused = new Dictionary<RefusalReason, int>();
        long otherFiles = 0;
        int tempFiles = 0;
        long links = 0;
        long inaccessible = 0;
        long visited = 0;
        long eligibleBytes = 0;
        bool truncated = false;
        bool completed = true;
        long lastReport = Environment.TickCount64;

        var stack = new Stack<string>();
        stack.Push(start);

        while (stack.Count > 0)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completed = false;
                break;
            }

            string dir = stack.Pop();
            List<DirectoryEntry> entries;
            try
            {
                entries = [.. fs.EnumerateDirectory(dir)];
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                inaccessible++;
                continue;
            }

            foreach (DirectoryEntry e in entries)
            {
                visited++;
                string full = dir.EndsWith('\\') ? dir + e.Name : dir + "\\" + e.Name;

                if (e.IsDirectory || (e.Attributes & FileAttributes.Directory) != 0)
                {
                    if ((e.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        links++;
                    }
                    else if (full.Length < PathGuard.MaxPathChars - 12)
                    {
                        stack.Push(full);
                    }

                    continue;
                }

                if (!FilePolicy.HasAllowedExtension(e.Name))
                {
                    otherFiles++;
                    continue;
                }

                tempFiles++;
                var shallow = new FileFacts
                {
                    Path = full,
                    Exists = true,
                    IsDirectory = false,
                    Attributes = e.Attributes,
                    Length = e.Length,
                    CreationTimeUtc = e.CreatedUtc,
                    LastWriteTimeUtc = e.ModifiedUtc,
                    IsDeep = false,
                };

                PolicyDecision quick = policy.Evaluate(shallow, requireDeepFacts: false);
                if (!quick.Eligible)
                {
                    Count(refused, quick.Reason);
                    continue;
                }

                FileFacts deep = fs.Probe(quick.NormalizedPath!);
                PolicyDecision decision = policy.Evaluate(deep);
                if (!decision.Eligible)
                {
                    Count(refused, decision.Reason);
                    continue;
                }

                if (candidates.Count >= maxCandidates)
                {
                    truncated = true;
                    continue;
                }

                string normalized = decision.NormalizedPath!;
                candidates.Add(new CleanupCandidate(
                    normalized,
                    normalized[(policy.TempRoot.Length + 1)..],
                    deep.Length,
                    deep.CreationTimeUtc,
                    deep.LastWriteTimeUtc,
                    policy.AgeDays(deep),
                    deep.Identity));
                eligibleBytes += deep.Length;
            }

            if (progress is not null && Environment.TickCount64 - lastReport >= 150)
            {
                lastReport = Environment.TickCount64;
                progress.Report(new CandidateScanProgress(visited, candidates.Count, dir));
            }
        }

        progress?.Report(new CandidateScanProgress(visited, candidates.Count, null));
        candidates.Sort(static (a, b) => b.Length.CompareTo(a.Length));
        var stats = new CandidateStats(tempFiles, otherFiles, candidates.Count, eligibleBytes, refused, links, inaccessible, truncated);
        return new CandidateScanResult(candidates, stats, policy.TempRoot, completed);
    }

    private static void Count(Dictionary<RefusalReason, int> map, RefusalReason reason) =>
        map[reason] = map.GetValueOrDefault(reason) + 1;
}

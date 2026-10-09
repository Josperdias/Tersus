using Tersus.Core.IO;
using Tersus.Core.Policy;

namespace Tersus.Core.Cleanup;

/// <summary>
/// Turns a selection into a plan WITHOUT changing anything: every file is looked at again, judged by the policy,
/// and compared with what the user saw at analysis time. The plan is what the UI shows as the "simulation".
/// The selection helpers (everything / by goal) can only choose among files the policy already approved;
/// they never relax a rule to reach a number.
/// </summary>
public sealed class CleanupPlanner(FilePolicy policy, IFileSystem fs)
{
    public CleanupPlan Simulate(IEnumerable<CleanupCandidate> selection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var items = new List<PlanItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CleanupCandidate candidate in selection)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seen.Add(candidate.Path))
            {
                continue;
            }

            FileFacts facts = fs.Probe(candidate.Path);
            PolicyDecision decision = policy.Evaluate(facts);
            if (decision.Eligible && ChangedSinceAnalysis(candidate, facts))
            {
                decision = PolicyDecision.Refuse(RefusalReason.ChangedSinceAnalysis);
            }

            items.Add(new PlanItem(candidate, decision, facts));
        }

        return new CleanupPlan(items, policy.UtcNow);
    }

    /// <summary>"Limpeza rápida": every approved candidate, pre-selected; the user still reviews and confirms.</summary>
    public static CleanupSelection ChooseAll(IReadOnlyList<CleanupCandidate> candidates)
    {
        long total = candidates.Sum(c => c.Length);
        return new CleanupSelection([.. candidates], total, true, 0, total);
    }

    /// <summary>
    /// "Limpeza por objetivo": picks the oldest approved files first (older means more certainly stale) until the logical size
    /// reaches the goal. If the approved files cannot reach it, says so and reports the shortfall instead of widening the rules.
    /// </summary>
    public static CleanupSelection ChooseForGoal(IReadOnlyList<CleanupCandidate> candidates, long targetBytes)
    {
        if (targetBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetBytes));
        }

        long available = candidates.Sum(c => c.Length);
        var chosen = new List<CleanupCandidate>();
        long sum = 0;
        if (targetBytes > 0)
        {
            foreach (CleanupCandidate c in candidates
                .OrderByDescending(c => c.AgeDays)
                .ThenByDescending(c => c.Length)
                .ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase))
            {
                if (sum >= targetBytes)
                {
                    break;
                }

                chosen.Add(c);
                sum += c.Length;
            }
        }

        bool reached = sum >= targetBytes;
        return new CleanupSelection(chosen, sum, reached, reached ? 0 : targetBytes - sum, available);
    }

    internal static bool ChangedSinceAnalysis(CleanupCandidate candidate, FileFacts facts)
    {
        if (facts.Length != candidate.Length
            || facts.LastWriteTimeUtc != candidate.ModifiedUtc
            || facts.CreationTimeUtc != candidate.CreatedUtc)
        {
            return true;
        }

        return candidate.Identity is not null && facts.Identity is not null && candidate.Identity != facts.Identity;
    }
}

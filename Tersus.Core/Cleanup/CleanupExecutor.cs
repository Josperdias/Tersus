using System.Diagnostics;
using System.Globalization;
using Tersus.Core.IO;
using Tersus.Core.Policy;

namespace Tersus.Core.Cleanup;

/// <summary>
/// Sends the files of a confirmed plan to the Recycle Bin, one at a time, re-checking everything first.
/// Safety properties (each one is covered by tests):
/// <list type="number">
/// <item>nothing happens without a confirmation that matches this exact plan;</item>
/// <item>the Recycle Bin is checked before the first file (a disabled or unusable bin means nothing is moved);</item>
/// <item>each file is held open (no writers, no replacement) while it is re-validated by the full policy and compared with the
/// analysis snapshot (size, dates, identity); anything that changed, is in use or looks different is skipped;</item>
/// <item>only <see cref="IRecycler.Recycle"/> moves data, and it must prove the file reached the bin; a file that vanishes
/// without proof stops the whole batch immediately;</item>
/// <item>there is no code path that deletes permanently or empties the bin; cancellation leaves the rest untouched.</item>
/// </list>
/// </summary>
public sealed class CleanupExecutor(FilePolicy policy, IFileSystem fs, IRecycler recycler, ICleanupLog? log = null)
{
    private readonly ICleanupLog _log = log ?? NullCleanupLog.Instance;

    public CleanupReport Execute(
        CleanupPlan plan,
        ExecutionConfirmation? confirmation,
        IProgress<ExecutionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var clock = Stopwatch.StartNew();
        DateTime started = policy.UtcNow;
        var results = new List<CleanupItemResult>(plan.Items.Count);
        string preflightMessage = string.Empty;
        long? freeBefore = null;
        bool aborted = false;
        string? abortReason = null;

        Log($"# Tersus - limpeza em {started.ToString("o", CultureInfo.InvariantCulture)} - plano {plan.Id} - {plan.Approved.Count} aprovado(s), {plan.Refused.Count} recusado(s)");

        if (confirmation is null || !confirmation.Matches(plan))
        {
            aborted = true;
            abortReason = "Sem confirmação explícita do usuário para este plano; nada foi alterado.";
            foreach (PlanItem item in plan.Items)
            {
                results.Add(Skipped(item, RefusalReason.NotConfirmed));
            }
        }
        else if (plan.Approved.Count == 0)
        {
            foreach (PlanItem item in plan.Items)
            {
                results.Add(Skipped(item, item.Decision.Reason));
            }
        }
        else
        {
            RecyclerPreflight preflight = recycler.Preflight(policy.TempRoot);
            preflightMessage = preflight.Message;
            Log("# Lixeira: " + preflight.Message);
            if (!preflight.Ready)
            {
                aborted = true;
                abortReason = "A Lixeira não está pronta: " + preflight.Message + " Nada foi movido.";
                foreach (PlanItem item in plan.Items)
                {
                    results.Add(Skipped(item, RefusalReason.RecycleBinUnavailable));
                }
            }
            else
            {
                freeBefore = fs.GetVolumeSpace(policy.TempRoot)?.FreeBytes;
                int done = 0;
                foreach (PlanItem item in plan.Items)
                {
                    progress?.Report(new ExecutionProgress(done++, plan.Items.Count, item.Candidate.Path));

                    if (!item.Approved)
                    {
                        results.Add(Skipped(item, item.Decision.Reason, item.Decision.Message));
                    }
                    else if (aborted)
                    {
                        results.Add(Skipped(item, RefusalReason.NotAttempted));
                    }
                    else if (cancellationToken.IsCancellationRequested)
                    {
                        results.Add(Skipped(item, RefusalReason.Cancelled));
                    }
                    else
                    {
                        CleanupItemResult result = ProcessOne(item, preflight);
                        results.Add(result);
                        if (result.Outcome == CleanupItemOutcome.UnprovenPossiblePermanentDeletion)
                        {
                            aborted = true;
                            abortReason = "Um arquivo saiu da pasta sem que a entrada correspondente fosse encontrada na Lixeira. "
                                + "Por precaução a operação foi interrompida; verifique a Lixeira do Windows.";
                        }
                    }
                }

                progress?.Report(new ExecutionProgress(plan.Items.Count, plan.Items.Count, null));
            }
        }

        long? freeAfter = freeBefore is null ? null : fs.GetVolumeSpace(policy.TempRoot)?.FreeBytes;
        clock.Stop();
        var report = new CleanupReport(plan, results, started, clock.Elapsed, aborted, abortReason, preflightMessage, freeBefore, freeAfter, _log.Path);
        Log(string.Create(CultureInfo.InvariantCulture, $"# Resumo: {report.MovedCount} enviado(s) à Lixeira ({report.MovedBytes} bytes lógicos), {report.SkippedCount} ignorado(s), {report.FailedCount} com falha{(aborted ? " - INTERROMPIDO: " + abortReason : string.Empty)}"));
        return report;
    }

    private CleanupItemResult ProcessOne(PlanItem item, RecyclerPreflight preflight)
    {
        CleanupCandidate c = item.Candidate;
        try
        {
            FileFacts facts = fs.TryHold(c.Path, out IDisposable? hold);
            using (hold)
            {
                PolicyDecision decision = policy.Evaluate(facts);
                if (hold is null || !decision.Eligible)
                {
                    RefusalReason reason = decision.Eligible ? RefusalReason.ProbeFailed : decision.Reason;
                    return Record(c, CleanupItemOutcome.Skipped, reason, RefusalText.Describe(reason));
                }

                if (CleanupPlanner.ChangedSinceAnalysis(c, facts))
                {
                    return Record(c, CleanupItemOutcome.Skipped, RefusalReason.ChangedSinceAnalysis, RefusalText.Describe(RefusalReason.ChangedSinceAnalysis));
                }

                if (preflight.BinCapacityBytes is long capacity && facts.Length > capacity)
                {
                    return Record(c, CleanupItemOutcome.Skipped, RefusalReason.ExceedsRecycleBinCapacity, RefusalText.Describe(RefusalReason.ExceedsRecycleBinCapacity));
                }

                RecycleOutcome outcome = recycler.Recycle(decision.NormalizedPath!, facts.Length);
                return outcome.Status switch
                {
                    RecycleStatus.Recycled => Record(c, CleanupItemOutcome.MovedToRecycleBin, RefusalReason.None, outcome.Message),
                    RecycleStatus.GoneWithoutProof => Record(c, CleanupItemOutcome.UnprovenPossiblePermanentDeletion, RefusalReason.None, outcome.Message),
                    RecycleStatus.NotMoved => Record(c, CleanupItemOutcome.Failed, RefusalReason.InUse, outcome.Message),
                    _ => Record(c, CleanupItemOutcome.Failed, RefusalReason.ProbeFailed, outcome.Message),
                };
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Record(c, CleanupItemOutcome.Failed, RefusalReason.ProbeFailed, "Erro inesperado: " + ex.Message);
        }
    }

    private CleanupItemResult Record(CleanupCandidate c, CleanupItemOutcome outcome, RefusalReason reason, string message)
    {
        Log(string.Create(CultureInfo.InvariantCulture, $"{outcome}\t{c.Length}\t{c.Path}\t{reason}\t{message}"));
        return new CleanupItemResult(c.Path, c.Length, outcome, reason, message);
    }

    private CleanupItemResult Skipped(PlanItem item, RefusalReason reason, string? message = null) =>
        Record(item.Candidate, CleanupItemOutcome.Skipped, reason, message ?? RefusalText.Describe(reason));

    private void Log(string line)
    {
        try
        {
            _log.Append(line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A full disk or a locked log must never change what happens to user files.
        }
    }
}

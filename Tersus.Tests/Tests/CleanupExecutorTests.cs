using Tersus.Core.Cleanup;
using Tersus.Core.IO;
using Tersus.Core.Policy;
using Tersus.Tests.Framework;
using Tersus.Tests.Support;

namespace Tersus.Tests;

[Category("cleanup")]
[Category("security")]
public class CleanupExecutorTests
{
    private static CleanupFixture TwoFiles(out string a, out string b)
    {
        var f = new CleanupFixture();
        a = f.AddOld("a.tmp", 100);
        b = f.AddOld(@"sub\b.temp", 200);
        return f;
    }

    [Test]
    public void Nothing_happens_without_a_confirmation()
    {
        CleanupFixture f = TwoFiles(out _, out _);
        CleanupPlan plan = f.PlanAll();
        CleanupReport r = f.NewExecutor().Execute(plan, null);
        Assert.True(r.Aborted);
        Assert.Equal(0, r.MovedCount);
        Assert.Equal(0, f.Recycler.RecycleCalls.Count);
        Assert.Equal(0, f.Recycler.PreflightCalls);
        Assert.Equal(2, f.Fs.Files.Count());
        Assert.True(r.Items.All(i => i.Reason == RefusalReason.NotConfirmed));
    }

    [Test]
    public void A_confirmation_for_another_plan_is_not_accepted()
    {
        CleanupFixture f = TwoFiles(out _, out _);
        CleanupPlan plan = f.PlanAll();
        CleanupPlan other = f.Planner.Simulate(f.Scan().Candidates.Take(1));
        CleanupReport r = f.NewExecutor().Execute(plan, f.Confirm(other));
        Assert.True(r.Aborted);
        Assert.Equal(0, f.Recycler.RecycleCalls.Count);
        Assert.Equal(2, f.Fs.Files.Count());
        var forged = new ExecutionConfirmation(plan.Id, "0000", CleanupFixture.Now);
        Assert.Equal(0, f.NewExecutor().Execute(plan, forged).MovedCount);
    }

    [Test]
    public void Happy_path_moves_every_approved_file_and_reports_logical_bytes()
    {
        CleanupFixture f = TwoFiles(out string a, out string b);
        f.Fs.Space = new VolumeSpace(1000, 500);
        CleanupPlan plan = f.PlanAll();
        CleanupReport r = f.NewExecutor().Execute(plan, f.Confirm(plan));
        Assert.False(r.Aborted);
        Assert.Equal(2, r.MovedCount);
        Assert.Equal(300L, r.MovedBytes);
        Assert.Equal(0, r.SkippedCount);
        Assert.Equal(0, r.FailedCount);
        Assert.False(f.Fs.Exists(a));
        Assert.False(f.Fs.Exists(b));
        Assert.Equal(2, f.Recycler.Bin.Count);
        Assert.Equal(1, f.Recycler.PreflightCalls);
        Assert.Equal(0, f.Fs.OpenHolds, "every hold must be released");
        Assert.Equal(500L, r.FreeBytesBefore);
        Assert.True(f.Log.Lines.Count >= 4);
        Assert.True(f.Log.Lines.Any(l => l.Contains(a, StringComparison.Ordinal)));
    }

    [Test]
    public void Files_that_changed_after_the_simulation_are_skipped_not_moved()
    {
        CleanupFixture f = TwoFiles(out string a, out string b);
        CleanupPlan plan = f.PlanAll();
        f.Fs.Update(a, x => x with { LastWriteTimeUtc = CleanupFixture.Now });
        f.Fs.Update(b, x => x with { Identity = new FileIdentity(7, 7, 7) });
        CleanupReport r = f.NewExecutor().Execute(plan, f.Confirm(plan));
        Assert.Equal(0, r.MovedCount);
        Assert.Equal(2, r.SkippedCount);
        Assert.Equal(0, f.Recycler.RecycleCalls.Count);
        Assert.True(f.Fs.Exists(a) && f.Fs.Exists(b));
        Assert.Equal(RefusalReason.TooNew, r.Items.Single(i => i.Path == a).Reason);
        Assert.Equal(RefusalReason.ChangedSinceAnalysis, r.Items.Single(i => i.Path == b).Reason);
    }

    [Test]
    public void A_file_swapped_for_a_link_between_simulation_and_execution_is_never_touched()
    {
        CleanupFixture f = TwoFiles(out string a, out _);
        CleanupPlan plan = f.PlanAll();
        f.Fs.Update(a, x => x with { Attributes = FileAttributes.ReparsePoint | FileAttributes.Archive, FinalPath = @"C:\Users\tester\Documents\important.docx" });
        CleanupReport r = f.NewExecutor().Execute(plan, f.Confirm(plan));
        Assert.False(f.Recycler.RecycleCalls.Contains(a));
        Assert.Equal(RefusalReason.ReparsePoint, r.Items.Single(i => i.Path == a).Reason);
        Assert.True(f.Fs.Exists(a));
        Assert.Equal(1, r.MovedCount, "the other, untouched file is still processed");
    }

    [Test]
    public void A_path_that_starts_resolving_through_a_junction_is_refused()
    {
        CleanupFixture f = TwoFiles(out string a, out _);
        CleanupPlan plan = f.PlanAll();
        f.Fs.Update(a, x => x with { FinalPath = @"C:\Users\tester\Documents\important.tmp" });
        CleanupReport r = f.NewExecutor().Execute(plan, f.Confirm(plan));
        Assert.Equal(RefusalReason.IndirectPath, r.Items.Single(i => i.Path == a).Reason);
        Assert.False(f.Recycler.RecycleCalls.Contains(a));
    }

    [Test]
    public void Locked_denied_and_vanished_files_are_skipped_while_the_rest_continues()
    {
        var f = new CleanupFixture();
        string locked = f.AddOld("locked.tmp");
        string denied = f.AddOld("denied.tmp");
        string gone = f.AddOld("gone.tmp");
        string fine = f.AddOld("fine.tmp");
        CleanupPlan plan = f.PlanAll();
        f.Fs.Lock(locked);
        f.Fs.DenyAccess(denied);
        f.Fs.Remove(gone);
        CleanupReport r = f.NewExecutor().Execute(plan, f.Confirm(plan));
        Assert.Equal(RefusalReason.InUse, r.Items.Single(i => i.Path == locked).Reason);
        Assert.Equal(RefusalReason.AccessDenied, r.Items.Single(i => i.Path == denied).Reason);
        Assert.Equal(RefusalReason.NotFound, r.Items.Single(i => i.Path == gone).Reason);
        Assert.Equal(1, r.MovedCount);
        Assert.SequenceEqual(new[] { fine }, f.Recycler.RecycleCalls);
        Assert.Equal(0, f.Fs.OpenHolds);
    }

    [Test]
    public void An_unusable_recycle_bin_stops_everything_before_any_file_is_touched()
    {
        CleanupFixture f = TwoFiles(out _, out _);
        f.Recycler.PreflightReady = false;
        f.Recycler.PreflightMessage = "Lixeira desativada.";
        CleanupPlan plan = f.PlanAll();
        CleanupReport r = f.NewExecutor().Execute(plan, f.Confirm(plan));
        Assert.True(r.Aborted);
        Assert.Equal(0, f.Recycler.RecycleCalls.Count);
        Assert.Equal(0, f.Fs.HoldCalls);
        Assert.Equal(2, f.Fs.Files.Count());
        Assert.True(r.Items.All(i => i.Reason == RefusalReason.RecycleBinUnavailable));
        Assert.Contains(r.AbortReason!, "Lixeira desativada.");
    }

    [Test]
    public void Files_bigger_than_the_bin_capacity_are_skipped_because_windows_would_delete_them_permanently()
    {
        var f = new CleanupFixture();
        f.Recycler.Capacity = 150;
        string small = f.AddOld("small.tmp", 100);
        string big = f.AddOld("big.tmp", 151);
        CleanupPlan plan = f.PlanAll();
        CleanupReport r = f.NewExecutor().Execute(plan, f.Confirm(plan));
        Assert.Equal(RefusalReason.ExceedsRecycleBinCapacity, r.Items.Single(i => i.Path == big).Reason);
        Assert.True(f.Fs.Exists(big));
        Assert.False(f.Fs.Exists(small));
    }

    [Test]
    public void A_failure_on_one_file_does_not_stop_the_others()
    {
        CleanupFixture f = TwoFiles(out string a, out string b);
        f.Recycler.FailPaths.Add(a);
        CleanupPlan plan = f.PlanAll();
        CleanupReport r = f.NewExecutor().Execute(plan, f.Confirm(plan));
        Assert.Equal(1, r.FailedCount);
        Assert.Equal(1, r.MovedCount);
        Assert.True(f.Fs.Exists(a));
        Assert.False(f.Fs.Exists(b));
        Assert.False(r.Aborted);
    }

    [Test]
    public void An_exception_inside_the_recycler_is_contained_and_the_hold_is_released()
    {
        CleanupFixture f = TwoFiles(out string a, out _);
        f.Recycler.ThrowPaths.Add(a);
        CleanupPlan plan = f.PlanAll();
        CleanupReport r = f.NewExecutor().Execute(plan, f.Confirm(plan));
        Assert.Equal(CleanupItemOutcome.Failed, r.Items.Single(i => i.Path == a).Outcome);
        Assert.Equal(0, f.Fs.OpenHolds);
        Assert.Equal(1, r.MovedCount);
    }

    [Test]
    public void A_file_that_vanishes_without_proof_stops_the_whole_batch_immediately()
    {
        var f = new CleanupFixture();
        string first = f.AddOld("1.tmp", 300);
        string second = f.AddOld("2.tmp", 200);
        string third = f.AddOld("3.tmp", 100);
        f.Recycler.UnprovenPaths.Add(second);
        CleanupPlan plan = f.PlanAll();
        CleanupReport r = f.NewExecutor().Execute(plan, f.Confirm(plan));
        Assert.True(r.Aborted);
        Assert.True(r.HasUnprovenDeletion);
        Assert.Equal(CleanupItemOutcome.MovedToRecycleBin, r.Items.Single(i => i.Path == first).Outcome);
        Assert.Equal(CleanupItemOutcome.UnprovenPossiblePermanentDeletion, r.Items.Single(i => i.Path == second).Outcome);
        Assert.Equal(RefusalReason.NotAttempted, r.Items.Single(i => i.Path == third).Reason);
        Assert.True(f.Fs.Exists(third), "files after the anomaly must be left alone");
        Assert.Contains(r.AbortReason!, "Lixeira");
    }

    [Test]
    public void Cancellation_leaves_the_remaining_files_untouched()
    {
        var f = new CleanupFixture();
        var paths = Enumerable.Range(1, 5).Select(i => f.AddOld($"f{i}.tmp", 10 * i)).ToList();
        CleanupPlan plan = f.PlanAll();
        using var cts = new CancellationTokenSource();
        int moved = 0;
        f.Recycler.BeforeRecycle = _ =>
        {
            if (++moved == 2)
            {
                cts.Cancel();
            }
        };
        CleanupReport r = f.NewExecutor().Execute(plan, f.Confirm(plan), null, cts.Token);
        Assert.Equal(2, r.MovedCount);
        Assert.Equal(3, r.Items.Count(i => i.Reason == RefusalReason.Cancelled));
        Assert.Equal(3, f.Fs.Files.Count());
        Assert.Equal(0, f.Fs.OpenHolds);
    }

    [Test]
    public void Refused_plan_items_are_never_handed_to_the_recycler()
    {
        var f = new CleanupFixture();
        f.AddOld("ok.tmp");
        string fresh = f.AddOld("fresh.tmp", 10, 1);
        CleanupCandidate forged = new(fresh, "fresh.tmp", 10, CleanupFixture.Now.AddDays(-1), CleanupFixture.Now.AddDays(-1), 1, null);
        CleanupPlan plan = f.Planner.Simulate(f.Scan().Candidates.Append(forged));
        CleanupReport r = f.NewExecutor().Execute(plan, f.Confirm(plan));
        Assert.False(f.Recycler.RecycleCalls.Contains(fresh));
        Assert.Equal(1, r.MovedCount);
        Assert.Equal(1, r.SkippedCount);
    }

    [Test]
    public void An_empty_plan_is_a_no_op_that_does_not_even_consult_the_recycle_bin()
    {
        var f = new CleanupFixture();
        f.AddOld("fresh.tmp", 10, 1);
        CleanupPlan plan = f.PlanAll();
        CleanupReport r = f.NewExecutor().Execute(plan, f.Confirm(plan));
        Assert.Equal(0, r.MovedCount);
        Assert.Equal(0, f.Recycler.PreflightCalls);
    }

    [Test]
    public void The_policy_is_reapplied_with_the_executors_own_clock()
    {
        // Plan today, execute "tomorrow" with a stricter policy window: the executor judges with the policy it was built with.
        CleanupFixture f = TwoFiles(out string a, out _);
        CleanupPlan plan = f.PlanAll();
        var strict = new FilePolicy(CleanupFixture.Root, f.Clock, TimeSpan.FromDays(45));
        var executor = new CleanupExecutor(strict, f.Fs, f.Recycler, f.Log);
        CleanupReport r = executor.Execute(plan, f.Confirm(plan));
        Assert.Equal(0, r.MovedCount);
        Assert.True(f.Fs.Exists(a));
    }

    [Test]
    public void The_log_failing_never_changes_what_happens_to_files()
    {
        CleanupFixture f = TwoFiles(out _, out _);
        CleanupPlan plan = f.PlanAll();
        var executor = new CleanupExecutor(f.Policy, f.Fs, f.Recycler, new ThrowingLog());
        CleanupReport r = executor.Execute(plan, f.Confirm(plan));
        Assert.Equal(2, r.MovedCount);
    }

    private sealed class ThrowingLog : ICleanupLog
    {
        public string? Path => null;

        public void Append(string line) => throw new IOException("disk full");
    }
}

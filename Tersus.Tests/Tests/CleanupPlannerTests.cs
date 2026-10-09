using Tersus.Core.Cleanup;
using Tersus.Core.IO;
using Tersus.Core.Policy;
using Tersus.Tests.Framework;
using Tersus.Tests.Support;

namespace Tersus.Tests;

[Category("cleanup")]
public class CleanupPlannerTests
{
    private static CleanupCandidate Candidate(CleanupFixture f, string path) =>
        f.Scan().Candidates.Single(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase));

    [Test]
    public void Simulation_approves_what_is_still_valid_and_touches_nothing()
    {
        var f = new CleanupFixture();
        f.AddOld("a.tmp", 100);
        f.AddOld("b.temp", 200);
        CleanupPlan plan = f.PlanAll();
        Assert.Equal(2, plan.Approved.Count);
        Assert.Equal(300L, plan.ApprovedBytes);
        Assert.Equal(0, plan.Refused.Count);
        Assert.Equal(2, f.Fs.Files.Count());
        Assert.Equal(0, f.Fs.HoldCalls);
        Assert.Equal(0, f.Recycler.RecycleCalls.Count);
        Assert.Equal(0, f.Recycler.PreflightCalls);
    }

    [Test]
    public void Detects_every_kind_of_change_since_the_analysis()
    {
        var f = new CleanupFixture();
        string touched = f.AddOld("touched.tmp");
        string grown = f.AddOld("grown.tmp", 100);
        string swapped = f.AddOld("swapped.tmp");
        string recreated = f.AddOld("recreated.tmp");
        string gone = f.AddOld("gone.tmp");
        string stable = f.AddOld("stable.tmp");
        var candidates = f.Scan().Candidates;

        f.Fs.Update(touched, x => x with { LastWriteTimeUtc = CleanupFixture.Now.AddHours(-1) });
        f.Fs.Update(grown, x => x with { Length = 101 });
        f.Fs.Update(swapped, x => x with { Identity = new FileIdentity(1, 999_999, 0) });
        f.Fs.Update(recreated, x => x with { CreationTimeUtc = CleanupFixture.Now.AddDays(-29) });
        f.Fs.Remove(gone);

        CleanupPlan plan = f.Planner.Simulate(candidates);
        PlanItem Item(string p) => plan.Items.Single(i => i.Candidate.Path == p);
        Assert.Equal(RefusalReason.TooNew, Item(touched).Decision.Reason);
        Assert.Equal(RefusalReason.ChangedSinceAnalysis, Item(grown).Decision.Reason);
        Assert.Equal(RefusalReason.ChangedSinceAnalysis, Item(swapped).Decision.Reason);
        Assert.Equal(RefusalReason.ChangedSinceAnalysis, Item(recreated).Decision.Reason);
        Assert.Equal(RefusalReason.NotFound, Item(gone).Decision.Reason);
        Assert.True(Item(stable).Approved);
        Assert.Equal(1, plan.Approved.Count);
    }

    [Test]
    public void A_file_replaced_by_a_link_or_folder_after_analysis_is_refused()
    {
        var f = new CleanupFixture();
        string link = f.AddOld("becomes-link.tmp");
        string dir = f.AddOld("becomes-dir.tmp");
        var candidates = f.Scan().Candidates;
        f.Fs.Update(link, x => x with { Attributes = x.Attributes | FileAttributes.ReparsePoint });
        f.Fs.Update(dir, x => x with { IsDirectory = true, Attributes = FileAttributes.Directory });
        CleanupPlan plan = f.Planner.Simulate(candidates);
        Assert.Equal(0, plan.Approved.Count);
        Assert.Equal(RefusalReason.ReparsePoint, plan.Items.Single(i => i.Candidate.Path == link).Decision.Reason);
        Assert.Equal(RefusalReason.IsDirectory, plan.Items.Single(i => i.Candidate.Path == dir).Decision.Reason);
    }

    [Test]
    public void Paths_outside_temp_that_sneak_into_a_selection_are_refused()
    {
        var f = new CleanupFixture();
        f.AddOld("legit.tmp");
        CleanupCandidate legit = f.Scan().Candidates[0];
        string victim = @"C:\Users\tester\Documents\thesis.tmp";
        f.Fs.AddFile(victim, 10, CleanupFixture.Now.AddDays(-90), CleanupFixture.Now.AddDays(-90));
        var forged = new CleanupCandidate(victim, "thesis.tmp", 10, CleanupFixture.Now.AddDays(-90), CleanupFixture.Now.AddDays(-90), 90, null);
        var forged2 = new CleanupCandidate(@"C:\Windows\System32\config\SAM", "SAM", 10, CleanupFixture.Now.AddDays(-90), CleanupFixture.Now.AddDays(-90), 90, null);
        CleanupPlan plan = f.Planner.Simulate([legit, forged, forged2]);
        Assert.Equal(1, plan.Approved.Count);
        Assert.Equal(RefusalReason.OutsideTemp, plan.Items.Single(i => i.Candidate.Path == victim).Decision.Reason);
        Assert.Equal(RefusalReason.OutsideTemp, plan.Items.Single(i => i.Candidate.Path.EndsWith("SAM", StringComparison.Ordinal)).Decision.Reason);
    }

    [Test]
    public void Duplicate_entries_in_a_selection_are_collapsed()
    {
        var f = new CleanupFixture();
        f.AddOld("a.tmp", 10);
        CleanupCandidate c = f.Scan().Candidates[0];
        CleanupPlan plan = f.Planner.Simulate([c, c, c with { }]);
        Assert.Equal(1, plan.Items.Count);
        Assert.Equal(10L, plan.ApprovedBytes);
    }

    [Test]
    public void Fingerprint_identifies_the_exact_approved_set_regardless_of_order()
    {
        var f = new CleanupFixture();
        f.AddOld("a.tmp", 10);
        f.AddOld("b.tmp", 20);
        f.AddOld("c.tmp", 30);
        var all = f.Scan().Candidates;
        CleanupPlan p1 = f.Planner.Simulate(all);
        CleanupPlan p2 = f.Planner.Simulate(all.Reverse());
        CleanupPlan p3 = f.Planner.Simulate(all.Take(2));
        Assert.Equal(p1.Fingerprint, p2.Fingerprint);
        Assert.NotEqual(p1.Fingerprint, p3.Fingerprint);
        Assert.NotEqual(p1.Id, p2.Id);
        Assert.Equal(64, p1.Fingerprint.Length);
    }

    [Test]
    public void Goal_selection_takes_the_oldest_first_and_stops_when_reached()
    {
        var f = new CleanupFixture();
        f.AddOld("young.tmp", 500, 20);
        f.AddOld("middle.tmp", 400, 100);
        f.AddOld("ancient.tmp", 300, 900);
        var candidates = f.Scan().Candidates;
        CleanupSelection s = CleanupPlanner.ChooseForGoal(candidates, 600);
        Assert.SequenceEqual(new[] { "ancient.tmp", "middle.tmp" }, s.Items.Select(c => c.Name));
        Assert.True(s.GoalReached);
        Assert.Equal(700L, s.TotalBytes);
        Assert.Equal(0L, s.ShortfallBytes);
        Assert.Equal(1200L, s.AvailableBytes);
    }

    [Test]
    public void Goal_selection_reports_the_shortfall_instead_of_widening_the_rules()
    {
        var f = new CleanupFixture();
        f.AddOld("only.tmp", 100, 50);
        f.AddOld("fresh.tmp", 99_999, 1);
        f.AddOld("keep.docx", 99_999, 400);
        var candidates = f.Scan().Candidates;
        CleanupSelection s = CleanupPlanner.ChooseForGoal(candidates, 10_000);
        Assert.False(s.GoalReached);
        Assert.Equal(1, s.Items.Count);
        Assert.Equal(9_900L, s.ShortfallBytes);
        Assert.Equal(100L, s.AvailableBytes);
    }

    [Test]
    public void Goal_edge_cases_zero_exact_and_negative()
    {
        var f = new CleanupFixture();
        f.AddOld("a.tmp", 100, 50);
        var candidates = f.Scan().Candidates;
        Assert.Equal(0, CleanupPlanner.ChooseForGoal(candidates, 0).Items.Count);
        Assert.True(CleanupPlanner.ChooseForGoal(candidates, 0).GoalReached);
        Assert.True(CleanupPlanner.ChooseForGoal(candidates, 100).GoalReached);
        Assert.False(CleanupPlanner.ChooseForGoal(candidates, 101).GoalReached);
        Assert.Throws<ArgumentOutOfRangeException>(() => CleanupPlanner.ChooseForGoal(candidates, -1));
    }

    [Test]
    public void Choose_all_selects_every_candidate()
    {
        var f = new CleanupFixture();
        f.AddOld("a.tmp", 10);
        f.AddOld("b.tmp", 20);
        CleanupSelection s = CleanupPlanner.ChooseAll(f.Scan().Candidates);
        Assert.Equal(2, s.Items.Count);
        Assert.Equal(30L, s.TotalBytes);
        Assert.True(s.GoalReached);
    }

    [Test]
    public void Candidate_helper_finds_the_expected_item()
    {
        var f = new CleanupFixture();
        string p = f.AddOld("x.tmp");
        Assert.Equal(p, Candidate(f, p).Path);
    }
}

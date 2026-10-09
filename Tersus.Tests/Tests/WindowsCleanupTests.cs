using System.Runtime.Versioning;
using System.Security.Principal;
using Tersus.Core.Cleanup;
using Tersus.Core.IO;
using Tersus.Core.Policy;
using Tersus.Core.Storage;
using Tersus.Core.Windows;
using Tersus.Tests.Framework;
using Tersus.Tests.Support;

namespace Tersus.Tests;

/// <summary>
/// End-to-end safety tests on a REAL Windows file system and the REAL Recycle Bin, inside the real TEMP folder (so the production policy
/// applies exactly as it does for users). They run in the disposable GitHub-hosted Windows VM. Each test creates only files of its own
/// under a unique sandbox folder.
/// </summary>
[WindowsOnly]
[SupportedOSPlatform("windows")]
[Category("windows")]
[Category("security")]
public class WindowsCleanupTests
{
    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            Sb = new WinSandbox();
            Stack = WinSandbox.Services(out string data, Sb);
            DataFolder = data;
            Assert.NotNull(Stack.Policy, "The cleanup policy is unavailable on this machine: " + Stack.Problem);
            Stack.Files.Paths.EnsureCreated();
        }

        public WinSandbox Sb { get; }

        public CleanupServices.Stack Stack { get; }

        public string DataFolder { get; }

        public FilePolicy Policy => Stack.Policy!;

        public IFileSystem Fs => Stack.FileSystem;

        public CandidateScanResult Scan() => new CleanupCandidateScanner(Policy, Fs).Scan(startFolder: Sb.Root);

        public CleanupPlan Plan(IEnumerable<CleanupCandidate> c) => new CleanupPlanner(Policy, Fs).Simulate(c);

        public CleanupReport Execute(CleanupPlan plan, CancellationToken ct = default, IProgress<ExecutionProgress>? progress = null)
        {
            var log = new FileCleanupLog(Stack.Files, Stack.Files.Paths.NewCleanupLogPath(DateTime.Now));
            return new CleanupExecutor(Policy, Fs, Stack.Recycler, log).Execute(plan, ExecutionConfirmation.For(plan, Policy.UtcNow), progress, ct);
        }

        public CleanupReport ScanPlanExecute() => Execute(Plan(Scan().Candidates));

        /// <summary>A candidate record for a path, built from what is on disk right now (as if it had been found by a scan).</summary>
        public CleanupCandidate Forge(string path)
        {
            FileFacts f = Fs.Probe(path);
            return new CleanupCandidate(path, Path.GetFileName(path), f.Length, f.CreationTimeUtc, f.LastWriteTimeUtc, 30, f.Identity);
        }

        public void Dispose() => Sb.Dispose();
    }

    private static string Item(CleanupReport r, string path) =>
        string.Join(" ", r.Items.Where(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)).Select(i => $"{i.Outcome}/{i.Reason}"));

    private static CleanupItemResult ResultFor(CleanupReport r, string path) =>
        r.Items.Single(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));

    [Test]
    public void The_policy_is_available_for_the_current_user()
    {
        using var rig = new Rig();
        Assert.Null(rig.Stack.Problem, rig.Stack.Problem);
        Assert.True(PathGuard.IsStrictlyUnder(rig.Policy.TempRoot, rig.Sb.Root), $"sandbox {rig.Sb.Root} must be inside TEMP {rig.Policy.TempRoot}");
    }

    [Test]
    public void Preflight_proves_the_recycle_bin_works_with_a_self_test_file()
    {
        using var rig = new Rig();
        RecyclerPreflight pre = rig.Stack.Recycler.Preflight(rig.Policy.TempRoot);
        Assert.True(pre.Ready, pre.Message);
        Assert.Contains(pre.Message, "verificada");
        string canaryDir = rig.Stack.Files.Paths.CanaryDirectory;
        Assert.Equal(0, Directory.Exists(canaryDir) ? Directory.EnumerateFiles(canaryDir).Count() : 0, "the self-test file must have left its folder");
        Assert.True(WinSandbox.BinEntryCount() > 0, "the Recycle Bin must contain at least the self-test record");
    }

    [Test]
    public void Full_cycle_moves_only_what_the_policy_allows_and_everything_else_stays_byte_for_byte_intact()
    {
        using var rig = new Rig();
        WinSandbox sb = rig.Sb;

        // must be moved
        string oldTmp = sb.Old("old.tmp", "um");
        string oldTemp = sb.Old(@"sub\deeper\old2.TEMP", "dois");
        string unicode = sb.Old("relat\u00F3rio com espa\u00E7o e \u00E7\u00E3o \u00FC.tmp", "tres");
        string zero = sb.Create("zero.tmp", [], 30);
        string hidden = sb.Old("hidden.tmp", "quatro");
        sb.SetAttributes(hidden, FileAttributes.Hidden);
        string big = sb.Create("three-megabytes.tmp", new byte[3 * 1024 * 1024], 30);
        string[] expectMoved = [oldTmp, oldTemp, unicode, zero, hidden, big];

        // must stay
        string young = sb.Old("young.tmp", "novo", ageDays: 1);
        string docx = sb.Old("keep.docx", "documento importante", ageDays: 400);
        string noExt = sb.Old("noext", "x", ageDays: 400);
        string doubleExt = sb.Old("report.tmp.docx", "x", ageDays: 400);
        string readOnly = sb.Old("readonly.tmp");
        sb.SetAttributes(readOnly, FileAttributes.ReadOnly);
        string system = sb.Old("system.tmp");
        sb.SetAttributes(system, FileAttributes.System);
        string linkA = sb.Old("linkA.tmp");
        sb.HardLink(linkA, sb.Path_("linkB.tmp"));
        string[] expectStay = [young, docx, noExt, doubleExt, readOnly, system, linkA, sb.Path_("linkB.tmp")];
        var hashes = expectStay.ToDictionary(p => p, WinSandbox.HashOf, StringComparer.OrdinalIgnoreCase);

        int binBefore = WinSandbox.BinEntryCount();
        CandidateScanResult scan = rig.Scan();
        Assert.SequenceEqual(
            expectMoved.Select(p => p.ToUpperInvariant()).OrderBy(p => p, StringComparer.Ordinal),
            scan.Candidates.Select(c => c.Path.ToUpperInvariant()).OrderBy(p => p, StringComparer.Ordinal),
            "the scan must propose exactly the eligible files");
        Assert.True(scan.Stats.Refused.GetValueOrDefault(RefusalReason.TooNew) >= 1);
        Assert.True(scan.Stats.Refused.GetValueOrDefault(RefusalReason.HardLinked) >= 2);
        Assert.True(scan.Stats.Refused.GetValueOrDefault(RefusalReason.ReadOnly) >= 1);

        CleanupPlan plan = rig.Plan(scan.Candidates);
        Assert.Equal(expectMoved.Length, plan.Approved.Count);
        foreach (string p in expectMoved.Concat(expectStay))
        {
            Assert.True(File.Exists(p), "the simulation must not touch anything: " + p);
        }

        CleanupReport report = rig.Execute(plan);
        Assert.False(report.Aborted, report.AbortReason);
        Assert.Equal(expectMoved.Length, report.MovedCount, string.Join("; ", report.Items.Select(i => $"{Path.GetFileName(i.Path)}={i.Outcome}/{i.Reason}/{i.Message}")));
        Assert.Equal(0, report.FailedCount);

        foreach (string p in expectMoved)
        {
            Assert.False(File.Exists(p), "should have left its folder: " + p);
            Assert.True(WinSandbox.BinHasEntryFor(p), "independent proof: a Recycle Bin record must exist for " + p);
        }

        foreach (string p in expectStay)
        {
            Assert.True(File.Exists(p), "must be untouched: " + p);
            Assert.Equal(hashes[p], WinSandbox.HashOf(p), "content changed: " + p);
        }

        Assert.True(WinSandbox.BinEntryCount() >= binBefore + expectMoved.Length, "the Recycle Bin must keep growing, never shrink: nothing may empty it");
        Assert.Equal(scan.Candidates.Sum(c => c.Length), report.MovedBytes, "moved bytes are reported as LOGICAL size, not as space freed");
        Assert.True(report.FreeBytesBefore is not null && report.FreeBytesAfter is not null, "free space is measured, not assumed");
    }

    [Test]
    public void The_cleanup_log_has_one_line_per_file_and_a_summary()
    {
        using var rig = new Rig();
        rig.Sb.Old("a.tmp");
        rig.Sb.Old("b.tmp");
        rig.Sb.Old("fresh.tmp", ageDays: 1);
        CleanupReport r = rig.ScanPlanExecute();
        Assert.Equal(2, r.MovedCount);
        Assert.True(r.LogPath is not null && File.Exists(r.LogPath), "log file must exist");
        string[] lines = File.ReadAllLines(r.LogPath!);
        Assert.True(lines.Count(l => l.StartsWith("MovedToRecycleBin", StringComparison.Ordinal)) == 2, string.Join("\n", lines));
        Assert.True(lines.Any(l => l.StartsWith("# Resumo", StringComparison.Ordinal)));
    }

    [Test]
    public void Junction_and_symbolic_link_victims_outside_temp_are_never_touched()
    {
        using var rig = new Rig();
        string outside = rig.Sb.NewOutsideFolder();
        string victim1 = WinSandbox.CreateAt(Path.Combine(outside, "victim1.tmp"), [1, 2, 3], 90);
        string victim2 = WinSandbox.CreateAt(Path.Combine(outside, "victim2.tmp"), [4, 5, 6], 90);
        string victimFile = WinSandbox.CreateAt(Path.Combine(outside, "victim3.tmp"), [7, 8, 9], 90);
        string h1 = WinSandbox.HashOf(victim1);
        string h2 = WinSandbox.HashOf(victim2);
        string h3 = WinSandbox.HashOf(victimFile);
        bool junction = rig.Sb.TryJunction("junction", outside);
        bool symDir = rig.Sb.TrySymlinkDirectory("symdir", outside);
        bool symFile = rig.Sb.TrySymlinkFile("symfile.tmp", victimFile);
        Assert.SkipIf(!junction && !symDir && !symFile, "Cannot create any kind of link here.");
        rig.Sb.Old("legit.tmp");

        CandidateScanResult scan = rig.Scan();
        Assert.SequenceEqual(new[] { rig.Sb.Path_("legit.tmp").ToUpperInvariant() }, scan.Candidates.Select(c => c.Path.ToUpperInvariant()), "the scan must not enter links nor propose link files");
        Assert.True(scan.Stats.LinksSkipped >= (junction ? 1 : 0) + (symDir ? 1 : 0));

        // Forged candidates that reach the victims THROUGH the links must be refused by the deep check.
        var forged = new List<CleanupCandidate>();
        if (junction)
        {
            forged.Add(rig.Forge(rig.Sb.Path_(@"junction\victim1.tmp")));
        }

        if (symDir)
        {
            forged.Add(rig.Forge(rig.Sb.Path_(@"symdir\victim2.tmp")));
        }

        if (symFile)
        {
            forged.Add(rig.Forge(rig.Sb.Path_("symfile.tmp")));
        }

        CleanupPlan plan = rig.Plan([.. scan.Candidates, .. forged]);
        Assert.Equal(1, plan.Approved.Count, "only the legit file may be approved");
        CleanupReport report = rig.Execute(plan);
        Assert.Equal(1, report.MovedCount);
        Assert.True(File.Exists(victim1) && File.Exists(victim2) && File.Exists(victimFile), "victims outside TEMP must survive");
        Assert.Equal(h1, WinSandbox.HashOf(victim1));
        Assert.Equal(h2, WinSandbox.HashOf(victim2));
        Assert.Equal(h3, WinSandbox.HashOf(victimFile));
        foreach (CleanupCandidate c in forged)
        {
            RefusalReason reason = ResultFor(report, c.Path).Reason;
            Assert.True(reason is RefusalReason.IndirectPath or RefusalReason.ReparsePoint or RefusalReason.OutsideTemp, $"{c.Path}: {reason}");
        }
    }

    [Test]
    public void A_path_with_a_short_8dot3_alias_cannot_be_used_to_dodge_the_checks()
    {
        using var rig = new Rig();
        string file = rig.Sb.Old(@"Pasta Longa Para Alias\Arquivo Longo Para Alias.tmp");
        string alias = rig.Sb.ShortName(file);
        Assert.SkipIf(string.Equals(alias, file, StringComparison.OrdinalIgnoreCase), "8.3 names are disabled on this volume.");
        CleanupCandidate forged = rig.Forge(alias);
        CleanupPlan plan = rig.Plan([forged]);
        Assert.Equal(0, plan.Approved.Count, "the alias must not be approved: " + string.Join(",", plan.Items.Select(i => i.Decision.Reason)));
        Assert.True(File.Exists(file));
    }

    [Test]
    public void Hard_linked_temp_files_are_refused_and_both_names_survive()
    {
        using var rig = new Rig();
        string outside = rig.Sb.NewOutsideFolder();
        string important = WinSandbox.CreateAt(Path.Combine(outside, "important.dat"), [1, 2, 3, 4], 400);
        string trick = rig.Sb.Path_("trick.tmp");
        rig.Sb.HardLink(important, trick);
        File.SetCreationTimeUtc(trick, DateTime.UtcNow.AddDays(-90));
        File.SetLastWriteTimeUtc(trick, DateTime.UtcNow.AddDays(-90));
        CleanupReport r = rig.Execute(rig.Plan([rig.Forge(trick)]));
        Assert.Equal(0, r.MovedCount);
        Assert.True(File.Exists(trick) && File.Exists(important));
        Assert.Equal(RefusalReason.HardLinked, ResultFor(r, trick).Reason);
    }

    [Test]
    public void Anything_that_changes_between_the_simulation_and_the_execution_is_left_alone()
    {
        using var rig = new Rig();
        WinSandbox sb = rig.Sb;
        string outside = sb.NewOutsideFolder();
        string victim = WinSandbox.CreateAt(Path.Combine(outside, "victim.tmp"), [1, 2, 3], 90);

        string modified = sb.Old("modified.tmp", "antes");
        string replaced = sb.Old("replaced.tmp", "mesmo");
        string becomesDir = sb.Old("becomes-dir.tmp");
        string becomesLink = sb.Old("becomes-link.tmp");
        string deleted = sb.Old("deleted.tmp");
        string stable = sb.Old("stable.tmp");

        CleanupPlan plan = rig.Plan(rig.Scan().Candidates);
        Assert.Equal(6, plan.Approved.Count);

        File.AppendAllText(modified, " depois");
        DateTime c = File.GetCreationTimeUtc(replaced);
        DateTime w = File.GetLastWriteTimeUtc(replaced);
        File.Delete(replaced);
        File.WriteAllText(replaced, "mesmo");
        File.SetCreationTimeUtc(replaced, c);
        File.SetLastWriteTimeUtc(replaced, w);
        File.Delete(becomesDir);
        Directory.CreateDirectory(becomesDir);
        File.Delete(becomesLink);
        bool linked = sb.TrySymlinkFile("becomes-link.tmp", victim);
        File.Delete(deleted);

        CleanupReport r = rig.Execute(plan);
        Assert.False(r.Aborted, r.AbortReason);
        Assert.Equal(1, r.MovedCount, string.Join("; ", r.Items.Select(i => $"{Path.GetFileName(i.Path)}={i.Outcome}/{i.Reason}")));
        Assert.False(File.Exists(stable), "the unchanged file is still moved");
        Assert.True(File.Exists(modified), "modified after the simulation");
        Assert.True(File.Exists(replaced), "replaced after the simulation");
        Assert.True(Directory.Exists(becomesDir), "swapped for a folder");
        Assert.True(File.Exists(victim), "link target outside TEMP");
        if (linked)
        {
            Assert.True(File.Exists(becomesLink) || Directory.Exists(becomesLink), "the link itself was not removed");
        }

        Assert.True(ResultFor(r, modified).Reason is RefusalReason.TooNew or RefusalReason.ChangedSinceAnalysis, Item(r, modified));
        Assert.Equal(RefusalReason.ChangedSinceAnalysis, ResultFor(r, replaced).Reason);
        Assert.Equal(RefusalReason.IsDirectory, ResultFor(r, becomesDir).Reason);
        Assert.Equal(RefusalReason.NotFound, ResultFor(r, deleted).Reason);
        if (linked)
        {
            Assert.True(ResultFor(r, becomesLink).Outcome == CleanupItemOutcome.Skipped, Item(r, becomesLink));
        }
    }

    [Test]
    public void Files_that_other_programs_are_using_are_skipped()
    {
        using var rig = new Rig();
        string exclusive = rig.Sb.Old("exclusive.tmp");
        string writing = rig.Sb.Old("writing.tmp");
        string reading = rig.Sb.Old("reading.tmp");
        string free = rig.Sb.Old("free.tmp");
        CleanupPlan plan = rig.Plan(rig.Scan().Candidates);
        Assert.Equal(4, plan.Approved.Count);

        using var x = new FileStream(exclusive, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var w = new FileStream(writing, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        using var rd = new FileStream(reading, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        CleanupReport r = rig.Execute(plan);
        Assert.Equal(RefusalReason.InUse, ResultFor(r, exclusive).Reason, Item(r, exclusive));
        Assert.Equal(RefusalReason.InUse, ResultFor(r, writing).Reason, Item(r, writing));
        Assert.True(File.Exists(exclusive) && File.Exists(writing));
        Assert.False(File.Exists(free), "unrelated files are still processed");
        Assert.True(r.MovedCount >= 1);
    }

    [Test]
    public void A_file_that_becomes_unreadable_after_planning_is_skipped_without_crashing()
    {
        using var rig = new Rig();
        string denied = rig.Sb.Old("denied.tmp");
        string fine = rig.Sb.Old("fine.tmp");
        CleanupPlan plan = rig.Plan(rig.Scan().Candidates);
        rig.Sb.DenyCurrentUser(denied);
        CleanupReport r = rig.Execute(plan);
        Assert.Equal(RefusalReason.AccessDenied, ResultFor(r, denied).Reason, Item(r, denied));
        Assert.False(File.Exists(fine));
        rig.Sb.AllowAgain(denied);
        Assert.True(File.Exists(denied));
    }

    [Test]
    public void Forged_candidates_outside_temp_or_with_tricky_paths_are_refused()
    {
        using var rig = new Rig();
        string outside = rig.Sb.NewOutsideFolder();
        string docsLike = WinSandbox.CreateAt(Path.Combine(outside, "thesis.tmp"), [1], 365);
        string sibling = WinSandbox.CreateAt(rig.Policy.TempRoot + "-Tersus-sibling-" + Guid.NewGuid().ToString("N")[..6] + @"\x.tmp", [1], 365);
        try
        {
            string viaDots = rig.Sb.Path_(@"..\..\" + Path.GetFileName(outside) + @"\thesis.tmp");
            string legit = rig.Sb.Old("legit.tmp");
            var candidates = new List<CleanupCandidate>
            {
                rig.Forge(docsLike),
                rig.Forge(sibling),
                new(viaDots, "thesis.tmp", 1, DateTime.UtcNow.AddDays(-365), DateTime.UtcNow.AddDays(-365), 365, null),
                new(legit + ":stream", "legit.tmp:stream", 1, DateTime.UtcNow.AddDays(-365), DateTime.UtcNow.AddDays(-365), 365, null),
                new(legit + ".", "legit.tmp.", 1, DateTime.UtcNow.AddDays(-365), DateTime.UtcNow.AddDays(-365), 365, null),
                new(@"\\?\" + legit, "legit.tmp", 1, DateTime.UtcNow.AddDays(-365), DateTime.UtcNow.AddDays(-365), 365, null),
                new(@"C:\Windows\System32\config\SAM", "SAM", 1, DateTime.UtcNow.AddDays(-365), DateTime.UtcNow.AddDays(-365), 365, null),
                new(@"C:\Windows\notepad.exe", "notepad.exe", 1, DateTime.UtcNow.AddDays(-365), DateTime.UtcNow.AddDays(-365), 365, null),
            };
            CleanupPlan plan = rig.Plan(candidates);
            Assert.Equal(0, plan.Approved.Count, string.Join(" | ", plan.Items.Select(i => $"{i.Candidate.Path} => {i.Decision.Reason}")));
            CleanupReport r = rig.Execute(plan);
            Assert.Equal(0, r.MovedCount);
            Assert.True(File.Exists(docsLike) && File.Exists(sibling) && File.Exists(legit));
        }
        finally
        {
            string? siblingDir = Path.GetDirectoryName(sibling);
            if (siblingDir is not null && Directory.Exists(siblingDir))
            {
                Directory.Delete(siblingDir, recursive: true);
            }
        }
    }

    [Test]
    public void Cancelling_stops_after_the_file_in_progress_and_leaves_the_rest()
    {
        using var rig = new Rig();
        var paths = Enumerable.Range(1, 5).Select(i => rig.Sb.Old($"f{i}.tmp", new string('x', i * 10))).ToList();
        CleanupPlan plan = rig.Plan(rig.Scan().Candidates);
        using var cts = new CancellationTokenSource();
        var progress = new SynchronousProgress<ExecutionProgress>(p =>
        {
            if (p.Done == 2 && p.CurrentPath is not null)
            {
                cts.Cancel();
            }
        });
        CleanupReport r = rig.Execute(plan, cts.Token, progress);
        Assert.Equal(2, r.MovedCount);
        Assert.Equal(3, paths.Count(File.Exists));
        Assert.Equal(3, r.Items.Count(i => i.Reason == RefusalReason.Cancelled));
    }

    [Test]
    public void Size_and_path_limits_hold_on_the_real_file_system()
    {
        using var rig = new Rig();
        string over = rig.Sb.Path_("over-limit.tmp");
        using (var fs = new FileStream(over, FileMode.Create))
        {
            fs.SetLength((256L * 1024 * 1024) + 1);
        }

        File.SetCreationTimeUtc(over, DateTime.UtcNow.AddDays(-60));
        File.SetLastWriteTimeUtc(over, DateTime.UtcNow.AddDays(-60));
        string small = rig.Sb.Old("small.tmp");

        string deep = rig.Sb.Root;
        while (deep.Length < 275)
        {
            deep = Path.Combine(deep, new string('d', 40));
        }

        string longFile = Path.Combine(deep, "long.tmp");
        bool longOk = true;
        try
        {
            WinSandbox.CreateAt(longFile, [1], 60);
        }
        catch (Exception ex) when (ex is PathTooLongException or IOException or DirectoryNotFoundException)
        {
            longOk = false;
        }

        CandidateScanResult scan = rig.Scan();
        Assert.SequenceEqual(new[] { small.ToUpperInvariant() }, scan.Candidates.Select(c => c.Path.ToUpperInvariant()), "only the small file qualifies");
        Assert.True(scan.Stats.Refused.GetValueOrDefault(RefusalReason.TooLarge) >= 1);
        if (longOk)
        {
            CleanupCandidate forged = new(longFile, "long.tmp", 1, DateTime.UtcNow.AddDays(-60), DateTime.UtcNow.AddDays(-60), 60, null);
            CleanupPlan plan = rig.Plan([forged]);
            Assert.Equal(RefusalReason.InvalidPath, plan.Items[0].Decision.Reason);
            Assert.True(File.Exists(longFile));
        }

        CleanupReport r = rig.Execute(rig.Plan(scan.Candidates));
        Assert.Equal(1, r.MovedCount);
        Assert.True(File.Exists(over));
    }

    [Test]
    public void Folders_named_like_temp_files_and_their_contents_are_handled_correctly()
    {
        using var rig = new Rig();
        string inner = rig.Sb.Old(@"cache.tmp\inner.tmp");
        Assert.True(Directory.Exists(rig.Sb.Path_("cache.tmp")));
        CleanupReport r = rig.ScanPlanExecute();
        Assert.Equal(1, r.MovedCount);
        Assert.False(File.Exists(inner));
        Assert.True(Directory.Exists(rig.Sb.Path_("cache.tmp")), "the folder itself must never be removed");
    }

    [Test]
    public void The_recycle_bin_settings_on_this_machine_allow_recycling()
    {
        RecycleBinPolicy.Result r = RecycleBinSettings.Read(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!);
        Assert.False(r.Disabled, r.Reason);
    }

    [Test]
    public void The_cleanup_stack_never_uses_the_data_folder_as_a_temp_source()
    {
        using var rig = new Rig();
        Assert.False(PathGuard.IsStrictlyUnder(rig.Policy.TempRoot, rig.DataFolder), "data folder must not be under TEMP");
        string userSid = WindowsIdentity.GetCurrent().User!.Value;
        Assert.True(userSid.StartsWith("S-1-5-", StringComparison.Ordinal));
    }
}

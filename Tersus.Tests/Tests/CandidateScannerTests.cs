using Tersus.Core.Cleanup;
using Tersus.Core.IO;
using Tersus.Core.Policy;
using Tersus.Tests.Framework;
using Tersus.Tests.Support;

namespace Tersus.Tests;

[Category("cleanup")]
public class CandidateScannerTests
{
    [Test]
    public void Finds_only_old_tmp_and_temp_files_and_counts_everything_else()
    {
        var f = new CleanupFixture();
        f.AddOld("old.tmp", 100, 30);
        f.AddOld(@"sub\deeper\old.temp", 200, 400);
        f.AddOld("fresh.tmp", 300, 1);
        f.AddOld("document.docx", 400, 900);
        f.AddOld("readonly.tmp", 50, 30, FileAttributes.ReadOnly);
        f.AddOld("hardlinked.tmp", 60, 30, links: 3);

        CandidateScanResult r = f.Scan();
        Assert.True(r.Completed);
        Assert.Equal(2, r.Candidates.Count);
        Assert.SequenceEqual(new[] { 200L, 100L }, r.Candidates.Select(c => c.Length));
        Assert.Equal(CleanupFixture.Root + @"\sub\deeper\old.temp", r.Candidates[0].Path);
        Assert.Equal(@"sub\deeper\old.temp", r.Candidates[0].RelativePath);
        Assert.Equal(400, r.Candidates[0].AgeDays);
        Assert.Equal(5, r.Stats.TempFilesSeen);
        Assert.Equal(1L, r.Stats.OtherFilesSeen);
        Assert.Equal(300L, r.Stats.EligibleBytes);
        Assert.Equal(1, r.Stats.Refused[RefusalReason.TooNew]);
        Assert.Equal(1, r.Stats.Refused[RefusalReason.ReadOnly]);
        Assert.Equal(1, r.Stats.Refused[RefusalReason.HardLinked]);
        Assert.True(r.Candidates.All(c => c.Identity is not null));
    }

    [Test]
    public void Never_enters_folders_that_are_links_and_never_lists_their_content()
    {
        var f = new CleanupFixture();
        f.AddFolder("junction", FileAttributes.ReparsePoint, finalPath: @"C:\Users\tester\Documents");
        f.Fs.AddFile(CleanupFixture.Root + @"\junction\victim.tmp", 10, CleanupFixture.Now.AddDays(-99), CleanupFixture.Now.AddDays(-99));
        f.AddOld("normal.tmp");
        CandidateScanResult r = f.Scan();
        Assert.Equal(1, r.Candidates.Count);
        Assert.Equal(CleanupFixture.Root + @"\normal.tmp", r.Candidates[0].Path);
        Assert.Equal(1L, r.Stats.LinksSkipped);
    }

    [Test]
    public void A_folder_named_like_a_temp_file_is_traversed_never_proposed()
    {
        var f = new CleanupFixture();
        f.AddFolder("cache.tmp");
        f.AddOld(@"cache.tmp\inner.tmp");
        CandidateScanResult r = f.Scan();
        Assert.SequenceEqual(new[] { CleanupFixture.Root + @"\cache.tmp\inner.tmp" }, r.Candidates.Select(c => c.Path));
    }

    [Test]
    public void Unreadable_folders_are_counted_and_the_rest_is_still_scanned()
    {
        var f = new CleanupFixture();
        f.AddFolder("locked");
        f.AddOld(@"locked\hidden.tmp");
        f.Fs.DenyListing(CleanupFixture.Root + @"\locked");
        f.AddOld("open.tmp");
        CandidateScanResult r = f.Scan();
        Assert.Equal(1L, r.Stats.InaccessibleFolders);
        Assert.Equal(1, r.Candidates.Count);
    }

    [Test]
    public void Deep_check_rejections_are_attributed_to_the_right_reason()
    {
        var f = new CleanupFixture();
        string p = f.AddOld("aliased.tmp");
        f.Fs.Update(p, x => x with { FinalPath = @"C:\Elsewhere\aliased.tmp" });
        CandidateScanResult r = f.Scan();
        Assert.Equal(0, r.Candidates.Count);
        Assert.Equal(1, r.Stats.Refused[RefusalReason.IndirectPath]);
    }

    [Test]
    public void Cancellation_returns_what_was_found_so_far_marked_incomplete()
    {
        var f = new CleanupFixture();
        f.AddOld("a.tmp");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        CandidateScanResult r = f.Scan(cts.Token);
        Assert.False(r.Completed);
    }

    [Test]
    public void Truncates_at_the_candidate_cap_and_says_so()
    {
        var f = new CleanupFixture();
        for (int i = 0; i < 10; i++)
        {
            f.AddOld($"f{i}.tmp", 10 + i);
        }

        CandidateScanResult r = f.Scan(max: 4);
        Assert.Equal(4, r.Candidates.Count);
        Assert.True(r.Stats.Truncated);
    }

    [Test]
    public void The_start_folder_must_be_temp_or_inside_it()
    {
        var f = new CleanupFixture();
        f.AddFolder("only");
        f.AddOld(@"only\a.tmp");
        f.AddOld("b.tmp");
        var scanner = new CleanupCandidateScanner(f.Policy, f.Fs);
        Assert.Equal(1, scanner.Scan(startFolder: CleanupFixture.Root + @"\only").Candidates.Count);
        Assert.Equal(2, scanner.Scan(startFolder: CleanupFixture.Root).Candidates.Count);
        Assert.Throws<ArgumentException>(() => scanner.Scan(startFolder: @"C:\Users\tester\Documents"));
        Assert.Throws<ArgumentException>(() => scanner.Scan(startFolder: @"C:\Users\tester\AppData\Local"));
        Assert.Throws<ArgumentException>(() => scanner.Scan(startFolder: CleanupFixture.Root + @"2"));
        Assert.Throws<ArgumentException>(() => scanner.Scan(startFolder: CleanupFixture.Root + @"\..\Documents"));
    }

    [Test]
    public void Scanning_changes_nothing()
    {
        var f = new CleanupFixture();
        f.AddOld("a.tmp");
        f.AddOld(@"x\b.temp");
        string before = string.Join("|", f.Fs.Files.Select(x => x.Path + x.Length + x.LastWriteTimeUtc.Ticks).OrderBy(s => s, StringComparer.Ordinal));
        f.Scan();
        string after = string.Join("|", f.Fs.Files.Select(x => x.Path + x.Length + x.LastWriteTimeUtc.Ticks).OrderBy(s => s, StringComparer.Ordinal));
        Assert.Equal(before, after);
        Assert.Equal(0, f.Fs.OpenHolds);
        Assert.Equal(0, f.Recycler.RecycleCalls.Count);
    }
}

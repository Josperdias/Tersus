using Tersus.Core.Scanning;
using Tersus.Tests.Framework;
using Tersus.Tests.Support;

namespace Tersus.Tests;

[Category("scanner")]
public class StorageScannerTests
{
    private static ScanResult Scan(string root, ScanOptions? options = null, CancellationToken ct = default) =>
        new StorageScanner().Scan(root, options, null, ct);

    private static void CheckInvariants(SizeNode node, bool isRoot = true)
    {
        long childSum = node.Children.Sum(c => c.Bytes);
        Assert.Equal(node.Bytes, node.DirectFilesBytes + childSum + node.OtherBytes, $"size invariant broken at '{node.Name}'");
        long folderSum = node.Children.Sum(c => c.FolderCount + 1) + node.OtherFolders;
        Assert.Equal(node.FolderCount, folderSum, $"folder-count invariant broken at '{node.Name}'");
        for (int i = 1; i < node.Children.Count; i++)
        {
            Assert.True(node.Children[i - 1].Bytes >= node.Children[i].Bytes, "children must be sorted by size, largest first");
        }

        foreach (SizeNode c in node.Children)
        {
            Assert.True(ReferenceEquals(c.Parent, node), "parent link");
            CheckInvariants(c, isRoot: false);
        }

        Assert.True(isRoot || node.Bytes >= 0);
    }

    [Test]
    public void Counts_files_folders_and_bytes_exactly()
    {
        using var t = new TempDir("scan");
        t.WriteBytes(@"rootfile.bin", new byte[7]);
        t.WriteBytes(@"a/one.bin", new byte[10]);
        t.WriteBytes(@"a/two.bin", new byte[20]);
        t.WriteBytes(@"a/three.bin", new byte[30]);
        t.WriteBytes(@"a/sub/deep.bin", new byte[100]);
        t.WriteBytes(@"b/five.bin", new byte[5]);

        ScanResult r = Scan(t.Path);
        Assert.True(r.Completed);
        Assert.Equal(172L, r.TotalBytes);
        Assert.Equal(6L, r.FileCount);
        Assert.Equal(3L, r.FolderCount);
        Assert.Equal(7L, r.Tree.DirectFilesBytes);
        Assert.Equal(2, r.Tree.Children.Count);
        Assert.Equal("a", r.Tree.Children[0].Name);
        Assert.Equal(160L, r.Tree.Children[0].Bytes);
        Assert.Equal("b", r.Tree.Children[1].Name);
        Assert.Equal(5L, r.Tree.Children[1].Bytes);
        Assert.Equal(1L, r.Tree.Children[0].FolderCount);
        Assert.Equal(0, r.InaccessibleFolders);
        CheckInvariants(r.Tree);
    }

    [Test]
    public void Matches_an_independent_count_on_a_random_tree()
    {
        using var t = new TempDir("rand");
        TreeFactory f = TreeFactory.Build(t.Path, seed: 7, folders: 250, files: 1500);
        ScanResult r = Scan(t.Path, new ScanOptions { InitialNodeThresholdBytes = 0 });
        Assert.True(r.Completed);
        Assert.Equal(f.TotalBytes, r.TotalBytes);
        Assert.Equal((long)f.Files.Count, r.FileCount);
        Assert.Equal((long)f.Folders.Count, r.FolderCount);
        CheckInvariants(r.Tree);

        // Every first-level folder total matches an independent recursive sum.
        foreach (SizeNode first in r.Tree.Children)
        {
            string dir = Path.Combine(t.Path, first.Name);
            long expected = f.Files.Where(x => x.Path.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.Ordinal)).Sum(x => x.Length);
            Assert.Equal(expected, first.Bytes, $"first-level folder {first.Name}");
        }
    }

    [Test]
    public void Largest_files_are_exact_sorted_and_limited()
    {
        using var t = new TempDir("top");
        TreeFactory f = TreeFactory.Build(t.Path, seed: 11, folders: 40, files: 400, maxLength: 5000);
        ScanResult r = Scan(t.Path, new ScanOptions { LargestFilesCount = 25 });
        var expected = f.Files.OrderByDescending(x => x.Length).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase).Take(25).ToList();
        Assert.Equal(25, r.LargestFiles.Count);
        Assert.SequenceEqual(expected.Select(x => x.Length), r.LargestFiles.Select(x => x.Bytes));
        for (int i = 1; i < r.LargestFiles.Count; i++)
        {
            Assert.True(r.LargestFiles[i - 1].Bytes >= r.LargestFiles[i].Bytes);
        }

        Assert.True(r.LargestFiles.All(x => File.Exists(x.Path) && new FileInfo(x.Path).Length == x.Bytes));
    }

    [Test]
    public void Default_limit_is_300_largest_files()
    {
        using var t = new TempDir("top300");
        for (int i = 1; i <= 350; i++)
        {
            t.WriteBytes($"f{i}.bin", new byte[i]);
        }

        ScanResult r = Scan(t.Path);
        Assert.Equal(300, r.LargestFiles.Count);
        Assert.Equal(350L, r.LargestFiles[0].Bytes);
        Assert.Equal(51L, r.LargestFiles[^1].Bytes);
    }

    [Test]
    public void Empty_folder_gives_zero_not_an_error()
    {
        using var t = new TempDir("empty");
        ScanResult r = Scan(t.Path);
        Assert.True(r.Completed);
        Assert.Equal(0L, r.TotalBytes);
        Assert.Equal(0L, r.FileCount);
        Assert.Equal(0, r.LargestFiles.Count);
        Assert.Equal(0, r.Tree.Children.Count);
    }

    [Test]
    public void Hidden_dot_and_unicode_names_are_counted()
    {
        using var t = new TempDir("names");
        t.WriteBytes(".hidden", new byte[11]);
        t.WriteBytes("pasta com espaço/arquivo ção ü.txt", new byte[13]);
        t.WriteBytes("日本語/ファイル.dat", new byte[17]);
        t.WriteBytes("long/" + new string('x', 150) + ".bin", new byte[19]);
        ScanResult r = Scan(t.Path);
        Assert.Equal(60L, r.TotalBytes);
        Assert.Equal(4L, r.FileCount);
        Assert.Equal(3L, r.FolderCount);
    }

    [Test]
    public void Extension_totals_are_case_insensitive_and_include_the_no_extension_bucket()
    {
        using var t = new TempDir("ext");
        t.WriteBytes("a.TXT", new byte[10]);
        t.WriteBytes("b.txt", new byte[20]);
        t.WriteBytes("c.Txt", new byte[30]);
        t.WriteBytes("noext", new byte[5]);
        t.WriteBytes("trailing.", new byte[3]);
        ScanResult r = Scan(t.Path);
        ExtensionStat txt = r.Extensions.Single(e => e.Extension == ".txt");
        Assert.Equal(3L, txt.Files);
        Assert.Equal(60L, txt.Bytes);
        ExtensionStat none = r.Extensions.Single(e => e.Extension == string.Empty);
        Assert.Equal(2L, none.Files);
        Assert.Equal(8L, none.Bytes);
    }

    [Test]
    public void Never_modifies_the_tree_it_analyses()
    {
        using var t = new TempDir("readonly");
        TreeFactory.Build(t.Path, seed: 3, folders: 30, files: 200);
        string Snapshot() => string.Join("|", Directory.EnumerateFileSystemEntries(t.Path, "*", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => p + ":" + (File.Exists(p) ? new FileInfo(p).Length + ":" + File.GetLastWriteTimeUtc(p).Ticks : "dir")));
        string before = Snapshot();
        Scan(t.Path);
        Assert.Equal(before, Snapshot());
    }

    [Test]
    public void Does_not_follow_symbolic_links_or_loops()
    {
        using var outside = new TempDir("outside");
        using var t = new TempDir("links");
        outside.WriteBytes("huge-elsewhere.bin", new byte[100_000]);
        t.WriteBytes("real/file.bin", new byte[10]);
        string dirLink = t.Combine("link-to-outside");
        string fileLink = t.Combine("file-link.bin");
        string loop = t.Combine("real", "loop");
        try
        {
            Directory.CreateSymbolicLink(dirLink, outside.Path);
            File.CreateSymbolicLink(fileLink, outside.Combine("huge-elsewhere.bin"));
            Directory.CreateSymbolicLink(loop, t.Path);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Skip("Cannot create symbolic links in this environment: " + ex.Message);
        }

        // The scan runs on its own task with a deadline so a link loop would show up as "the scan hung", not as a mystery timeout.
        Task<ScanResult> task = Task.Run(() => Scan(t.Path));
        Assert.True(task.Wait(TimeSpan.FromSeconds(60)), "the scan did not finish in 60 s: it is following a link loop");
        ScanResult r = task.Result;
        Assert.True(r.Completed);
        Assert.Equal(10L, r.TotalBytes);
        Assert.Equal(1L, r.FileCount);
        Assert.True(r.LinksSkipped >= 3, $"expected 3 links skipped, got {r.LinksSkipped}");
        Assert.True(File.Exists(outside.Combine("huge-elsewhere.bin")), "link targets must be untouched");
    }

    [Test]
    public void A_pre_cancelled_token_returns_a_partial_result_instead_of_throwing()
    {
        using var t = new TempDir("cancel");
        TreeFactory.Build(t.Path, seed: 5, folders: 100, files: 500);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        ScanResult r = Scan(t.Path, ct: cts.Token);
        Assert.False(r.Completed);
        Assert.NotNull(r.Tree);
    }

    [Test]
    public void Cancelling_mid_scan_stops_quickly_and_reports_partial_first_level()
    {
        using var t = new TempDir("cancel2");
        TreeFactory.Build(t.Path, seed: 9, folders: 600, files: 6000, maxDepth: 5);
        using var cts = new CancellationTokenSource();
        int reports = 0;
        var progress = new SynchronousProgress<ScanProgress>(_ =>
        {
            if (Interlocked.Increment(ref reports) == 3)
            {
                cts.Cancel();
            }
        });
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ScanResult r = new StorageScanner().Scan(t.Path, new ScanOptions { ProgressIntervalMs = 0, MaxDegreeOfParallelism = 2 }, progress, cts.Token);
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), "cancellation must be prompt");
        if (!r.Completed)
        {
            Assert.True(r.Tree.Children.All(c => c.Children.Count == 0), "a partial result only lists first-level folders");
            Assert.True(r.TotalBytes >= 0);
        }
    }

    [Test]
    public void Reports_progress_with_non_decreasing_counters()
    {
        using var t = new TempDir("progress");
        TreeFactory.Build(t.Path, seed: 13, folders: 100, files: 800);
        var seen = new List<ScanProgress>();
        var progress = new SynchronousProgress<ScanProgress>(p =>
        {
            lock (seen)
            {
                seen.Add(p);
            }
        });
        ScanResult r = new StorageScanner().Scan(t.Path, new ScanOptions { ProgressIntervalMs = 0 }, progress);
        Assert.True(seen.Count >= 2, "expected several progress reports");
        ScanProgress last = seen[^1];
        Assert.Equal(r.FileCount, last.Files);
        Assert.Equal(r.TotalBytes, last.Bytes);
        long maxFiles = 0;
        foreach (ScanProgress p in seen.OrderBy(x => x.Elapsed))
        {
            Assert.True(p.Files >= 0 && p.Files <= r.FileCount);
            maxFiles = Math.Max(maxFiles, p.Files);
        }

        Assert.Equal(r.FileCount, maxFiles);
    }

    [Test]
    public void Node_budget_is_enforced_and_totals_stay_exact()
    {
        using var t = new TempDir("budget");
        TreeFactory f = TreeFactory.Build(t.Path, seed: 21, folders: 700, files: 3000, maxDepth: 8, maxLength: 3000);
        ScanResult r = Scan(t.Path, new ScanOptions { MaxRetainedNodes = 60, InitialNodeThresholdBytes = 0 });
        Assert.Equal(f.TotalBytes, r.TotalBytes);
        int nodes = r.Tree.Descendants().Count();
        int firstLevel = r.Tree.Children.Count;
        Assert.True(nodes <= 60 + firstLevel + 20, $"{nodes} retained nodes exceed the budget (first level {firstLevel})");
        CheckInvariants(r.Tree);
        Assert.True(r.Tree.Descendants().Any(n => n.OtherBytes > 0) || r.Tree.OtherBytes > 0, "pruned folders must be summarised, not lost");
    }

    [Test]
    public void Depth_limit_adds_a_note_and_never_overflows_the_stack()
    {
        using var t = new TempDir("deep");
        string path = t.Path;
        for (int i = 0; i < 40; i++)
        {
            path = Path.Combine(path, "d");
        }

        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, "bottom.bin"), new byte[9]);
        ScanResult shallow = Scan(t.Path, new ScanOptions { MaxDepth = 10 });
        Assert.True(shallow.Notes.Count > 0, "a depth cut-off must be reported");
        Assert.Equal(0L, shallow.TotalBytes);
        ScanResult deep = Scan(t.Path);
        Assert.Equal(9L, deep.TotalBytes);
        Assert.Equal(40L, deep.FolderCount);
    }

    [Test]
    public void Missing_root_is_an_error_the_caller_can_handle()
    {
        Assert.Throws<DirectoryNotFoundException>(() => Scan(Path.Combine(Path.GetTempPath(), "tersus-does-not-exist-" + Guid.NewGuid().ToString("N"))));
        Assert.Throws<ArgumentException>(() => Scan(" "));
    }

    [Test]
    public void Volume_information_is_filled_and_unaccounted_only_for_whole_drives()
    {
        using var t = new TempDir("volume");
        t.WriteBytes("x.bin", new byte[100]);
        ScanResult r = Scan(t.Path);
        Assert.True(r.VolumeTotalBytes is > 0);
        Assert.True(r.VolumeFreeBytes is >= 0);
        Assert.False(r.RootIsVolumeRoot);
        Assert.Null(r.UnaccountedBytes?.ToString());
    }

    [Test]
    public void Inaccessible_folders_are_counted_and_do_not_abort_the_scan()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Covered by the Windows integration tests (ACL deny).");
            return;
        }

        using var t = new TempDir("denied");
        t.WriteBytes("ok/file.bin", new byte[10]);
        string locked = t.Combine("locked");
        Directory.CreateDirectory(locked);
        File.WriteAllBytes(Path.Combine(locked, "secret.bin"), new byte[50]);
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            bool reallyDenied;
            try
            {
                _ = Directory.GetFileSystemEntries(locked);
                reallyDenied = false;
            }
            catch (UnauthorizedAccessException)
            {
                reallyDenied = true;
            }

            Assert.SkipIf(!reallyDenied, "Running with privileges that bypass directory permissions (e.g. root).");
            ScanResult r = Scan(t.Path);
            Assert.True(r.Completed);
            Assert.Equal(10L, r.TotalBytes);
            Assert.Equal(1L, r.InaccessibleFolders);
            Assert.Equal(1, r.InaccessibleSamples.Count);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Test]
    public void Scan_is_repeatable_and_thread_count_does_not_change_the_answer()
    {
        using var t = new TempDir("det");
        TreeFactory.Build(t.Path, seed: 33, folders: 120, files: 900);
        ScanResult one = Scan(t.Path, new ScanOptions { MaxDegreeOfParallelism = 1 });
        ScanResult four = Scan(t.Path, new ScanOptions { MaxDegreeOfParallelism = 4 });
        Assert.Equal(one.TotalBytes, four.TotalBytes);
        Assert.Equal(one.FileCount, four.FileCount);
        Assert.Equal(one.FolderCount, four.FolderCount);
        Assert.SequenceEqual(one.LargestFiles.Select(f => f.Bytes), four.LargestFiles.Select(f => f.Bytes));
    }
}

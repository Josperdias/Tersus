using System.Security.Cryptography;
using Tersus.Core.Duplicates;
using Tersus.Core.IO;
using Tersus.Tests.Framework;
using Tersus.Tests.Support;

namespace Tersus.Tests;

[Category("duplicates")]
public class DuplicateAnalyzerTests
{
    private static readonly DuplicateOptions Small = new() { MinFileBytes = 1024, SampleBytes = 4096 };

    private static byte[] Pattern(int seed, int length)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static string Snapshot(string dir) => string.Join("|", Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
        .OrderBy(p => p, StringComparer.Ordinal)
        .Select(p => p + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))) + ":" + File.GetLastWriteTimeUtc(p).Ticks));

    [Test]
    public void Groups_identical_content_and_separates_look_alikes()
    {
        using var t = new TempDir("dup");
        byte[] x = Pattern(1, 50_000);
        byte[] y = Pattern(2, 30_000);
        byte[] almostX = (byte[])x.Clone();
        almostX[^1] ^= 0xFF;
        t.WriteBytes("a/x1.bin", x);
        t.WriteBytes("b/x2.bin", x);
        t.WriteBytes("c/x-different-tail.bin", almostX);
        t.WriteBytes("y1.bin", y);
        t.WriteBytes("sub/y2.bin", y);
        t.WriteBytes("unique.bin", Pattern(3, 12_345));

        DuplicateResult r = new DuplicateAnalyzer().Find(t.Path, Small);
        Assert.True(r.Completed);
        Assert.Equal(2, r.Groups.Count);
        DuplicateGroup gx = r.Groups[0];
        Assert.Equal(50_000L, gx.Size);
        Assert.True(gx.Verified);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(x)), gx.Sha256);
        Assert.Equal(2, gx.Files.Count);
        Assert.True(gx.Files.All(f => f.Path.EndsWith("x1.bin", StringComparison.Ordinal) || f.Path.EndsWith("x2.bin", StringComparison.Ordinal)));
        Assert.Equal(50_000L, gx.PotentialBytes);
        Assert.Equal(30_000L, r.Groups[1].PotentialBytes);
        Assert.Equal(80_000L, r.VerifiedPotentialBytes);
        Assert.Equal(0L, r.UnverifiedPotentialBytes);
    }

    [Test]
    public void Files_that_only_differ_in_the_middle_are_not_reported_as_duplicates()
    {
        using var t = new TempDir("middle");
        byte[] a = Pattern(5, 300_000);
        byte[] b = (byte[])a.Clone();
        b[150_000] ^= 0x01; // same size, identical head and tail samples
        t.WriteBytes("a.bin", a);
        t.WriteBytes("b.bin", b);
        DuplicateResult r = new DuplicateAnalyzer().Find(t.Path, Small);
        Assert.Equal(0, r.Groups.Count);
        Assert.Equal(2L, r.Stats.FullyHashedFiles);
    }

    [Test]
    public void Three_copies_form_one_group_with_two_extra_copies_of_potential()
    {
        using var t = new TempDir("three");
        byte[] x = Pattern(8, 20_000);
        for (int i = 0; i < 3; i++)
        {
            t.WriteBytes($"d{i}/copy.bin", x);
        }

        DuplicateResult r = new DuplicateAnalyzer().Find(t.Path, Small);
        Assert.Equal(1, r.Groups.Count);
        Assert.Equal(3, r.Groups[0].Files.Count);
        Assert.Equal(40_000L, r.Groups[0].PotentialBytes);
    }

    [Test]
    public void Small_files_below_the_minimum_and_empty_files_are_ignored()
    {
        using var t = new TempDir("min");
        t.WriteBytes("s1.bin", Pattern(1, 500));
        t.WriteBytes("s2.bin", Pattern(1, 500));
        t.WriteBytes("e1.bin", []);
        t.WriteBytes("e2.bin", []);
        DuplicateResult r = new DuplicateAnalyzer().Find(t.Path, Small with { MinFileBytes = 0 });
        Assert.True(r.Groups.Any(g => g.Size == 500), "with minimum 0 the 500-byte pair is compared");
        Assert.False(r.Groups.Any(g => g.Size == 0), "empty files are never reported");
        DuplicateResult strict = new DuplicateAnalyzer().Find(t.Path, Small);
        Assert.Equal(0, strict.Groups.Count);
    }

    [Test]
    public void Never_modifies_anything()
    {
        using var t = new TempDir("ro");
        byte[] x = Pattern(11, 40_000);
        t.WriteBytes("a.bin", x);
        t.WriteBytes("b.bin", x);
        t.WriteBytes("c.bin", Pattern(12, 40_000));
        string before = Snapshot(t.Path);
        new DuplicateAnalyzer().Find(t.Path, Small);
        Assert.Equal(before, Snapshot(t.Path));
    }

    [Test]
    public void Hard_links_to_the_same_data_are_not_duplicates()
    {
        using var t = new TempDir("hl");
        byte[] x = Pattern(13, 40_000);
        string a = t.WriteBytes("a.bin", x);
        string b = t.WriteBytes("a-link.bin", x);
        string c = t.WriteBytes("c.bin", x);
        FileIdentity? Identity(string p) => p == a || p == b ? new FileIdentity(1, 42, 0) : new FileIdentity(1, 99, 0);
        DuplicateResult r = new DuplicateAnalyzer(Identity).Find(t.Path, Small);
        Assert.Equal(1, r.Groups.Count);
        Assert.Equal(2, r.Groups[0].Files.Count);
        Assert.Equal(1L, r.Stats.HardLinksIgnored);
        Assert.True(r.Groups[0].Files.Any(f => f.Path == c));
        DuplicateResult only = new DuplicateAnalyzer(p => p == c ? new FileIdentity(1, 5, 0) : new FileIdentity(1, 42, 0)).Find(t.Path, Small);
        Assert.Equal(1, only.Groups.Count);
    }

    [Test]
    public void The_read_budget_marks_the_overflow_as_not_verified_and_says_so()
    {
        using var t = new TempDir("budget");
        foreach ((int seed, int size) in new[] { (1, 90_000), (2, 60_000), (3, 30_000) })
        {
            byte[] x = Pattern(seed, size);
            t.WriteBytes($"g{seed}/one.bin", x);
            t.WriteBytes($"g{seed}/two.bin", x);
        }

        DuplicateResult r = new DuplicateAnalyzer().Find(t.Path, Small with { MaxFullyHashedFiles = 2 });
        Assert.Equal(3, r.Groups.Count);
        Assert.True(r.Groups[0].Verified);
        Assert.Equal(90_000L, r.Groups[0].Size);
        Assert.False(r.Groups[1].Verified);
        Assert.False(r.Groups[2].Verified);
        Assert.Null(r.Groups[1].Sha256);
        Assert.True(r.Stats.HashBudgetExhausted);
        Assert.Equal(90_000L, r.VerifiedPotentialBytes);
        Assert.Equal(90_000L, r.UnverifiedPotentialBytes);
        Assert.True(r.Warnings.Any(w => w.Contains("NÃO foi verificado", StringComparison.Ordinal)));

        DuplicateResult byBytes = new DuplicateAnalyzer().Find(t.Path, Small with { MaxFullyHashedBytes = 200_000 });
        Assert.True(byBytes.Stats.HashBudgetExhausted);
        Assert.True(byBytes.Groups.Any(g => !g.Verified));
    }

    [Test]
    public void When_there_are_too_many_files_the_largest_are_kept()
    {
        using var t = new TempDir("cap");
        foreach ((int seed, int size) in new[] { (1, 10_000), (2, 20_000), (3, 30_000) })
        {
            byte[] x = Pattern(seed, size);
            t.WriteBytes($"g{seed}/one.bin", x);
            t.WriteBytes($"g{seed}/two.bin", x);
        }

        DuplicateResult r = new DuplicateAnalyzer().Find(t.Path, Small with { MaxInventoryFiles = 4 });
        Assert.True(r.Stats.InventoryTruncated);
        Assert.SequenceEqual(new[] { 30_000L, 20_000L }, r.Groups.Select(g => g.Size));
        Assert.True(r.Warnings.Count > 0);
    }

    [Test]
    public void Links_are_not_followed()
    {
        using var outside = new TempDir("out");
        using var t = new TempDir("links");
        byte[] x = Pattern(21, 30_000);
        outside.WriteBytes("same.bin", x);
        t.WriteBytes("local.bin", x);
        try
        {
            Directory.CreateSymbolicLink(t.Combine("dir-link"), outside.Path);
            File.CreateSymbolicLink(t.Combine("file-link.bin"), outside.Combine("same.bin"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Skip("Cannot create symbolic links here: " + ex.Message);
        }

        DuplicateResult r = new DuplicateAnalyzer().Find(t.Path, Small);
        Assert.Equal(0, r.Groups.Count);
    }

    [Test]
    public void Cancellation_yields_an_incomplete_result_without_throwing()
    {
        using var t = new TempDir("cancel");
        byte[] x = Pattern(31, 30_000);
        t.WriteBytes("a.bin", x);
        t.WriteBytes("b.bin", x);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        DuplicateResult r = new DuplicateAnalyzer().Find(t.Path, Small, null, cts.Token);
        Assert.False(r.Completed);
    }

    [Test]
    public void Reports_progress_stages()
    {
        using var t = new TempDir("prog");
        byte[] x = Pattern(41, 30_000);
        t.WriteBytes("a.bin", x);
        t.WriteBytes("b.bin", x);
        var stages = new List<string>();
        var progress = new SynchronousProgress<DuplicateProgress>(p => stages.Add(p.Stage));
        new DuplicateAnalyzer().Find(t.Path, Small with { ProgressIntervalMs = 0 }, progress);
        Assert.Contains(stages, "Concluído");
        Assert.True(stages.Count >= 2);
    }

    [Test]
    public void Missing_folder_is_an_error_the_caller_can_handle()
    {
        Assert.Throws<DirectoryNotFoundException>(() => new DuplicateAnalyzer().Find(Path.Combine(Path.GetTempPath(), "tersus-nope-" + Guid.NewGuid().ToString("N"))));
    }
}

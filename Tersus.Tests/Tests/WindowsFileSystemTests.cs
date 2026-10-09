using System.Runtime.Versioning;
using Tersus.Core.IO;
using Tersus.Core.Policy;
using Tersus.Core.Windows;
using Tersus.Tests.Framework;
using Tersus.Tests.Support;

namespace Tersus.Tests;

[WindowsOnly]
[SupportedOSPlatform("windows")]
[Category("windows")]
public class WindowsFileSystemTests
{
    private readonly WindowsFileSystem _fs = new();

    [Test]
    public void Probe_reports_size_times_attributes_identity_and_final_path()
    {
        using var sb = new WinSandbox();
        string file = sb.Old(@"a\b.tmp", "123456", ageDays: 40);
        FileFacts f = _fs.Probe(file);
        Assert.True(f.Exists && f.IsDeep, f.ErrorDetail);
        Assert.False(f.IsDirectory);
        Assert.Equal(6L, f.Length);
        Assert.Equal(1u, f.HardLinkCount ?? 0);
        Assert.True(f.Identity is not null);
        Assert.True(Math.Abs((f.LastWriteTimeUtc - DateTime.UtcNow.AddDays(-40)).TotalMinutes) < 5, "write time");
        Assert.True(Math.Abs((f.CreationTimeUtc - DateTime.UtcNow.AddDays(-40)).TotalMinutes) < 5, "creation time");
        Assert.True(PathGuard.AreSame(PathGuard.StripExtendedPrefix(f.FinalPath!), file), $"final path {f.FinalPath} vs {file}");
    }

    [Test]
    public void Probe_of_missing_directory_and_denied_items_maps_errors()
    {
        using var sb = new WinSandbox();
        Assert.Equal(ProbeError.NotFound, _fs.Probe(sb.Path_("nope.tmp")).Error);
        Assert.Equal(ProbeError.NotFound, _fs.Probe(sb.Path_(@"no\such\dir\x.tmp")).Error);
        Directory.CreateDirectory(sb.Path_("dir.tmp"));
        FileFacts d = _fs.Probe(sb.Path_("dir.tmp"));
        Assert.True(d.Exists && d.IsDirectory);
        Assert.Equal(RefusalReason.IsDirectory, Policy(sb).Evaluate(d).Reason);
    }

    private static FilePolicy Policy(WinSandbox sb)
    {
        string temp = new WindowsFileSystem().Probe(Path.GetTempPath()).FinalPath!;
        return new FilePolicy(PathGuard.StripExtendedPrefix(temp).TrimEnd('\\'));
    }

    [Test]
    public void Short_8dot3_aliases_resolve_to_the_long_path_and_are_refused_as_indirect()
    {
        using var sb = new WinSandbox();
        string file = sb.Old(@"Pasta Com Nome Longo\Arquivo De Teste Longo.tmp");
        string shortPath = sb.ShortName(file);
        if (string.Equals(shortPath, file, StringComparison.OrdinalIgnoreCase))
        {
            Assert.Skip("8.3 short names are disabled on this volume.");
        }

        FileFacts f = _fs.Probe(shortPath);
        Assert.True(f.Exists);
        Assert.True(PathGuard.AreSame(PathGuard.StripExtendedPrefix(f.FinalPath!), file), "the handle must reveal the long path");
        PolicyDecision d = Policy(sb).Evaluate(f);
        Assert.False(d.Eligible);
        Assert.True(d.Reason is RefusalReason.IndirectPath or RefusalReason.InvalidPath or RefusalReason.OutsideTemp, d.Reason.ToString());
    }

    [Test]
    public void Hard_links_are_counted_on_every_name()
    {
        using var sb = new WinSandbox();
        string a = sb.Old("a.tmp");
        string b = sb.Path_("b.tmp");
        sb.HardLink(a, b);
        FileFacts fa = _fs.Probe(a);
        FileFacts fb = _fs.Probe(b);
        Assert.Equal(2u, fa.HardLinkCount ?? 0);
        Assert.Equal(2u, fb.HardLinkCount ?? 0);
        Assert.Equal(fa.Identity, fb.Identity);
        FilePolicy p = Policy(sb);
        Assert.Equal(RefusalReason.HardLinked, p.Evaluate(fa).Reason);
        Assert.Equal(RefusalReason.HardLinked, p.Evaluate(fb).Reason);
    }

    [Test]
    public void A_junction_in_the_middle_of_a_path_is_revealed_by_the_final_path()
    {
        using var sb = new WinSandbox();
        string outside = sb.NewOutsideFolder();
        string victim = WinSandbox.CreateAt(Path.Combine(outside, "victim.tmp"), [1, 2, 3]);
        if (!sb.TryJunction("junction", outside))
        {
            Assert.Skip("Cannot create a junction here.");
        }

        string viaJunction = sb.Path_(@"junction\victim.tmp");
        FileFacts f = _fs.Probe(viaJunction);
        Assert.True(f.Exists);
        Assert.True(PathGuard.AreSame(PathGuard.StripExtendedPrefix(f.FinalPath!), victim), $"expected {victim} but handle says {f.FinalPath}");
        Assert.Equal(RefusalReason.IndirectPath, Policy(sb).Evaluate(f).Reason);

        FileFacts junctionItself = _fs.Probe(sb.Path_("junction"));
        Assert.True(junctionItself.IsReparsePoint && junctionItself.IsDirectory, "the junction itself is a reparse point");
    }

    [Test]
    public void A_symbolic_link_file_is_opened_as_the_link_not_the_target()
    {
        using var sb = new WinSandbox();
        string outside = sb.NewOutsideFolder();
        string victim = WinSandbox.CreateAt(Path.Combine(outside, "victim.tmp"), [9, 9, 9, 9, 9]);
        if (!sb.TrySymlinkFile("link.tmp", victim))
        {
            Assert.Skip("Creating symbolic links needs privileges that are missing here.");
        }

        FileFacts f = _fs.Probe(sb.Path_("link.tmp"));
        Assert.True(f.IsReparsePoint, "must see the link itself");
        Assert.Equal(RefusalReason.ReparsePoint, Policy(sb).Evaluate(f).Reason);
        Assert.True(File.Exists(victim));
    }

    [Test]
    public void Hold_blocks_writers_but_still_allows_the_rename_the_recycle_bin_needs()
    {
        using var sb = new WinSandbox();
        string file = sb.Old("held.tmp");
        FileFacts facts = _fs.TryHold(file, out IDisposable? hold);
        Assert.True(hold is not null && facts.Exists, facts.ErrorDetail);
        using (hold)
        {
            Assert.Throws<IOException>(() => { using var w = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); }, "nobody may open it for writing while held");
            string moved = sb.Path_("moved.tmp");
            File.Move(file, moved);
            Assert.True(File.Exists(moved) && !File.Exists(file), "a rename must work while the hold is open");
        }
    }

    [Test]
    public void Hold_fails_when_another_program_has_the_file_open_for_writing_or_exclusively()
    {
        using var sb = new WinSandbox();
        string writer = sb.Old("writer.tmp");
        using (var w = new FileStream(writer, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            FileFacts f = _fs.TryHold(writer, out IDisposable? hold);
            Assert.True(hold is null, "a file being written must not be held");
            Assert.Equal(ProbeError.SharingViolation, f.Error);
            Assert.Equal(RefusalReason.InUse, Policy(sb).Evaluate(f).Reason);
        }

        string exclusive = sb.Old("exclusive.tmp");
        using (var x = new FileStream(exclusive, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            FileFacts f = _fs.TryHold(exclusive, out IDisposable? hold);
            Assert.True(hold is null);
            Assert.Equal(ProbeError.SharingViolation, f.Error);
        }

        FileFacts ok = _fs.TryHold(writer, out IDisposable? released);
        Assert.True(released is not null && ok.Exists, "after the other program lets go, the hold works");
        released!.Dispose();
    }

    [Test]
    public void A_reader_that_shares_everything_does_not_prevent_a_hold()
    {
        using var sb = new WinSandbox();
        string file = sb.Old("reader.tmp");
        using var r = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        FileFacts f = _fs.TryHold(file, out IDisposable? hold);
        Assert.True(hold is not null, f.ErrorDetail);
        hold!.Dispose();
    }

    [Test]
    public void Denied_access_is_reported_as_such()
    {
        using var sb = new WinSandbox();
        string file = sb.Old("denied.tmp");
        sb.DenyCurrentUser(file);
        FileFacts f = _fs.TryHold(file, out IDisposable? hold);
        Assert.True(hold is null);
        Assert.Equal(ProbeError.AccessDenied, f.Error);
        sb.AllowAgain(file);
    }

    [Test]
    public void Directory_listing_returns_attributes_sizes_and_times_and_lists_links_without_following_them()
    {
        using var sb = new WinSandbox();
        string outside = sb.NewOutsideFolder();
        WinSandbox.CreateAt(Path.Combine(outside, "inside-victim.tmp"), [1]);
        sb.Old("one.tmp", "abc");
        sb.Old(@"sub\two.temp", "abcdef");
        bool junction = sb.TryJunction("junction", outside);
        List<DirectoryEntry> entries = [.. _fs.EnumerateDirectory(sb.Root)];
        Assert.True(entries.Any(e => e.Name == "one.tmp" && !e.IsDirectory && e.Length == 3));
        Assert.True(entries.Any(e => e.Name == "sub" && e.IsDirectory));
        if (junction)
        {
            DirectoryEntry j = entries.Single(e => e.Name == "junction");
            Assert.True(j.IsDirectory && (j.Attributes & FileAttributes.ReparsePoint) != 0);
        }

        Assert.Throws<DirectoryNotFoundException>(() => _ = _fs.EnumerateDirectory(sb.Path_("missing")).ToList());
    }

    [Test]
    public void Directory_listing_of_a_denied_folder_throws_unauthorized()
    {
        using var sb = new WinSandbox();
        string dir = sb.Path_("locked");
        Directory.CreateDirectory(dir);
        sb.DenyCurrentUser(dir);
        Assert.Throws<UnauthorizedAccessException>(() => _ = _fs.EnumerateDirectory(dir).ToList());
        sb.AllowAgain(dir);
    }

    [Test]
    public void Volume_space_and_allocated_size_make_sense()
    {
        using var sb = new WinSandbox();
        VolumeSpace? v = _fs.GetVolumeSpace(sb.Root);
        Assert.True(v is { TotalBytes: > 0, FreeBytes: >= 0 });
        string file = sb.Create("sized.bin", new byte[100_000]);
        long? allocated = WindowsFileSystem.AllocatedSize(file);
        Assert.True(allocated is >= 100_000, "allocated size is at least the logical size for an ordinary file");
    }
}

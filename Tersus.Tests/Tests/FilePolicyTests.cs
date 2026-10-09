using Tersus.Core;
using Tersus.Core.IO;
using Tersus.Core.Policy;
using Tersus.Tests.Framework;
using Tersus.Tests.Support;

namespace Tersus.Tests;

[Category("policy")]
public class FilePolicyTests
{
    private const string Root = @"C:\Users\tester\AppData\Local\Temp";
    private static readonly DateTime Now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Old = Now.AddDays(-20);

    private static FilePolicy NewPolicy(TimeSpan? minAge = null, long? maxBytes = null) =>
        new(Root, new FixedClock(Now), minAge, maxBytes);

    private static PolicyDecision Check(FakeFileSystem fs, string path, FilePolicy? policy = null) =>
        (policy ?? NewPolicy()).Evaluate(fs.Probe(path));

    private static void AssertRefused(PolicyDecision d, RefusalReason expected)
    {
        Assert.False(d.Eligible, $"expected refusal {expected} but file was eligible");
        Assert.Equal(expected, d.Reason);
        Assert.True(d.Message.Length > 0, "refusals must carry a message");
    }

    [Test]
    public void Accepts_old_tmp_inside_temp()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(Root + @"\a.tmp", 1024, Old, Old);
        PolicyDecision d = Check(fs, Root + @"\a.tmp");
        Assert.True(d.Eligible, d.Message);
        Assert.Equal(Root + @"\a.tmp", d.NormalizedPath);
    }

    [Test]
    public void Accepts_both_extensions_in_any_case_and_nested_folders()
    {
        var fs = new FakeFileSystem();
        foreach (string name in new[] { "a.tmp", "A.TMP", "b.temp", "B.Temp", @"sub\deep\c.tmp", "report.docx.tmp" })
        {
            fs.AddFile(Root + @"\" + name, 10, Old, Old);
            Assert.True(Check(fs, Root + @"\" + name).Eligible, name);
        }
    }

    [Test]
    public void Refuses_every_other_extension()
    {
        var fs = new FakeFileSystem();
        foreach (string name in new[] { "a.docx", "a.tmp.exe", "a.tmpx", "a.tmp~", "a.log", "a", "a.temporary", "a.xtmp", "a.tmp.docx" })
        {
            fs.AddFile(Root + @"\" + name, 10, Old, Old);
            AssertRefused(Check(fs, Root + @"\" + name), RefusalReason.WrongExtension);
        }
    }

    [Test]
    public void Refuses_paths_outside_temp()
    {
        var fs = new FakeFileSystem();
        foreach (string p in new[]
        {
            @"C:\Users\tester\Documents\a.tmp",
            @"C:\Windows\Temp\a.tmp",
            @"C:\Users\tester\AppData\Local\Temp2\a.tmp",
            @"C:\Users\tester\AppData\Local\a.tmp",
            @"D:\Users\tester\AppData\Local\Temp\a.tmp",
            @"C:\a.tmp",
        })
        {
            fs.AddFile(p, 10, Old, Old);
            AssertRefused(Check(fs, p), RefusalReason.OutsideTemp);
        }
    }

    [Test]
    public void Refuses_the_temp_root_itself()
    {
        AssertRefused(NewPolicy().EvaluateName(Root), RefusalReason.OutsideTemp);
        AssertRefused(NewPolicy().EvaluateName(Root + @"\"), RefusalReason.OutsideTemp);
    }

    [Test]
    public void Refuses_parent_traversal_that_leaves_temp()
    {
        AssertRefused(NewPolicy().EvaluateName(Root + @"\..\..\Documents\a.tmp"), RefusalReason.OutsideTemp);
        AssertRefused(NewPolicy().EvaluateName(Root + @"\sub\..\..\x.tmp"), RefusalReason.OutsideTemp);
        // Traversal that stays inside is simply normalised.
        Assert.True(NewPolicy().EvaluateName(Root + @"\sub\..\x.tmp").Eligible);
    }

    [Test]
    public void Refuses_exotic_path_forms()
    {
        FilePolicy p = NewPolicy();
        foreach (string path in new[]
        {
            Root + @"\a.tmp:Zone.Identifier",
            Root + @"\a.tmp.",
            Root + @"\a.tmp ",
            @"\\?\" + Root + @"\a.tmp",
            @"\\server\share\Temp\a.tmp",
            Root + @"\CON.tmp",
            Root + @"\sub\nul.temp",
            @"..\a.tmp",
            "a.tmp",
        })
        {
            AssertRefused(p.EvaluateName(path), RefusalReason.InvalidPath);
        }
    }

    [Test]
    public void Refuses_a_path_longer_than_259_characters()
    {
        string path = Root + @"\" + new string('d', 250) + ".tmp";
        Assert.True(path.Length > 259);
        AssertRefused(NewPolicy().EvaluateName(path), RefusalReason.InvalidPath);
    }

    [Test]
    public void Age_boundary_is_exactly_14_days()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(Root + @"\exact.tmp", 10, Now.AddDays(-14), Now.AddDays(-14));
        fs.AddFile(Root + @"\almost.tmp", 10, Now.AddDays(-14).AddSeconds(1), Now.AddDays(-14).AddSeconds(1));
        fs.AddFile(Root + @"\fresh.tmp", 10, Now, Now);
        Assert.True(Check(fs, Root + @"\exact.tmp").Eligible);
        AssertRefused(Check(fs, Root + @"\almost.tmp"), RefusalReason.TooNew);
        AssertRefused(Check(fs, Root + @"\fresh.tmp"), RefusalReason.TooNew);
    }

    [Test]
    public void Both_creation_and_modification_must_be_old()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(Root + @"\recently-modified.tmp", 10, Old, Now.AddDays(-1));
        fs.AddFile(Root + @"\recently-created.tmp", 10, Now.AddDays(-1), Old);
        AssertRefused(Check(fs, Root + @"\recently-modified.tmp"), RefusalReason.TooNew);
        AssertRefused(Check(fs, Root + @"\recently-created.tmp"), RefusalReason.TooNew);
    }

    [Test]
    public void Refuses_timestamps_in_the_future_or_implausibly_old()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(Root + @"\future.tmp", 10, Old, Now.AddDays(30));
        fs.AddFile(Root + @"\future-created.tmp", 10, Now.AddDays(2), Old);
        fs.AddFile(Root + @"\zero.tmp", 10, DateTime.FromFileTimeUtc(0), Old);
        fs.AddFile(Root + @"\ancient.tmp", 10, Old, new DateTime(1985, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        foreach (string n in new[] { "future", "future-created", "zero", "ancient" })
        {
            AssertRefused(Check(fs, Root + @"\" + n + ".tmp"), RefusalReason.TimestampInvalid);
        }
    }

    [Test]
    public void Small_clock_skew_in_the_future_is_tolerated_only_when_the_file_is_otherwise_too_new()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(Root + @"\skew.tmp", 10, Old, Now.AddMinutes(3));
        AssertRefused(Check(fs, Root + @"\skew.tmp"), RefusalReason.TooNew);
    }

    [Test]
    public void Size_boundary_is_exactly_256_mib()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(Root + @"\exact.tmp", 256L * 1024 * 1024, Old, Old);
        fs.AddFile(Root + @"\over.tmp", (256L * 1024 * 1024) + 1, Old, Old);
        fs.AddFile(Root + @"\zero.tmp", 0, Old, Old);
        Assert.True(Check(fs, Root + @"\exact.tmp").Eligible);
        AssertRefused(Check(fs, Root + @"\over.tmp"), RefusalReason.TooLarge);
        Assert.True(Check(fs, Root + @"\zero.tmp").Eligible);
    }

    [Test]
    public void Refuses_directories_even_if_named_like_temp_files()
    {
        var fs = new FakeFileSystem();
        fs.AddDirectory(Root + @"\folder.tmp");
        AssertRefused(Check(fs, Root + @"\folder.tmp"), RefusalReason.IsDirectory);
    }

    [Test]
    public void Refuses_reparse_points_links_junctions_and_placeholders()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(Root + @"\link.tmp", 10, Old, Old, FileAttributes.Archive | FileAttributes.ReparsePoint);
        AssertRefused(Check(fs, Root + @"\link.tmp"), RefusalReason.ReparsePoint);
    }

    [Test]
    public void Refuses_system_readonly_offline_and_cloud_recall_attributes()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(Root + @"\sys.tmp", 10, Old, Old, FileAttributes.System);
        fs.AddFile(Root + @"\ro.tmp", 10, Old, Old, FileAttributes.ReadOnly);
        fs.AddFile(Root + @"\off.tmp", 10, Old, Old, FileAttributes.Offline);
        fs.AddFile(Root + @"\recall-open.tmp", 10, Old, Old, (FileAttributes)0x00040000);
        fs.AddFile(Root + @"\recall-data.tmp", 10, Old, Old, (FileAttributes)0x00400000);
        fs.AddFile(Root + @"\dev.tmp", 10, Old, Old, FileAttributes.Device);
        AssertRefused(Check(fs, Root + @"\sys.tmp"), RefusalReason.SystemFile);
        AssertRefused(Check(fs, Root + @"\ro.tmp"), RefusalReason.ReadOnly);
        AssertRefused(Check(fs, Root + @"\off.tmp"), RefusalReason.CloudPlaceholder);
        AssertRefused(Check(fs, Root + @"\recall-open.tmp"), RefusalReason.CloudPlaceholder);
        AssertRefused(Check(fs, Root + @"\recall-data.tmp"), RefusalReason.CloudPlaceholder);
        AssertRefused(Check(fs, Root + @"\dev.tmp"), RefusalReason.SystemFile);
    }

    [Test]
    public void Allows_harmless_attributes()
    {
        var fs = new FakeFileSystem();
        foreach ((string name, FileAttributes a) in new[]
        {
            ("hidden.tmp", FileAttributes.Hidden),
            ("temporary.tmp", FileAttributes.Temporary | FileAttributes.Archive),
            ("compressed.tmp", FileAttributes.Compressed),
            ("encrypted.tmp", FileAttributes.Encrypted),
            ("sparse.tmp", FileAttributes.SparseFile),
            ("notindexed.tmp", FileAttributes.NotContentIndexed),
        })
        {
            fs.AddFile(Root + @"\" + name, 10, Old, Old, a);
            Assert.True(Check(fs, Root + @"\" + name).Eligible, name);
        }
    }

    [Test]
    public void Refuses_hard_linked_files_and_unknown_link_counts()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(Root + @"\two.tmp", 10, Old, Old, hardLinks: 2);
        fs.AddFile(Root + @"\many.tmp", 10, Old, Old, hardLinks: 7);
        fs.AddFile(Root + @"\one.tmp", 10, Old, Old, hardLinks: 1);
        AssertRefused(Check(fs, Root + @"\two.tmp"), RefusalReason.HardLinked);
        AssertRefused(Check(fs, Root + @"\many.tmp"), RefusalReason.HardLinked);
        Assert.True(Check(fs, Root + @"\one.tmp").Eligible);
        fs.Update(Root + @"\one.tmp", f => f with { HardLinkCount = null });
        AssertRefused(Check(fs, Root + @"\one.tmp"), RefusalReason.ProbeFailed);
        fs.Update(Root + @"\one.tmp", f => f with { HardLinkCount = 0 });
        AssertRefused(Check(fs, Root + @"\one.tmp"), RefusalReason.ProbeFailed);
    }

    [Test]
    public void Refuses_when_the_real_path_differs_from_the_path_given()
    {
        var fs = new FakeFileSystem();
        // A junction/symlink in a parent folder: the handle reveals the real location.
        fs.AddFile(Root + @"\via-junction\victim.tmp", 10, Old, Old, finalPath: @"C:\Users\tester\Documents\victim.tmp");
        // Real target also inside TEMP but reached through a different name: still indirect.
        fs.AddFile(Root + @"\alias.tmp", 10, Old, Old, finalPath: Root + @"\real.tmp");
        // 8.3 short-name alias of a long name.
        fs.AddFile(@"C:\Users\TESTER~1\AppData\Local\Temp\a.tmp", 10, Old, Old, finalPath: Root + @"\a.tmp");
        AssertRefused(Check(fs, Root + @"\via-junction\victim.tmp"), RefusalReason.IndirectPath);
        AssertRefused(Check(fs, Root + @"\alias.tmp"), RefusalReason.IndirectPath);
        AssertRefused(Check(fs, @"C:\Users\TESTER~1\AppData\Local\Temp\a.tmp"), RefusalReason.OutsideTemp);
    }

    [Test]
    public void Accepts_final_paths_that_only_differ_by_case_or_extended_prefix()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(Root + @"\a.tmp", 10, Old, Old, finalPath: @"\\?\C:\USERS\TESTER\appdata\local\temp\A.TMP");
        Assert.True(Check(fs, Root + @"\a.tmp").Eligible);
    }

    [Test]
    public void Deep_facts_are_mandatory_before_acting_but_not_for_listing()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(Root + @"\a.tmp", 10, Old, Old);
        fs.Update(Root + @"\a.tmp", f => f with { IsDeep = false });
        FilePolicy policy = NewPolicy();
        AssertRefused(policy.Evaluate(fs.Probe(Root + @"\a.tmp")), RefusalReason.ProbeFailed);
        Assert.True(policy.Evaluate(fs.Probe(Root + @"\a.tmp"), requireDeepFacts: false).Eligible);
        fs.Update(Root + @"\a.tmp", f => f with { IsDeep = true, FinalPath = null });
        AssertRefused(policy.Evaluate(fs.Probe(Root + @"\a.tmp")), RefusalReason.ProbeFailed);
        fs.Update(Root + @"\a.tmp", f => f with { FinalPath = Root + @"\a.tmp", Identity = null });
        AssertRefused(policy.Evaluate(fs.Probe(Root + @"\a.tmp")), RefusalReason.ProbeFailed);
    }

    [Test]
    public void Maps_probe_errors_to_reasons()
    {
        var fs = new FakeFileSystem();
        fs.AddFile(Root + @"\denied.tmp", 10, Old, Old);
        fs.DenyAccess(Root + @"\denied.tmp");
        AssertRefused(Check(fs, Root + @"\denied.tmp"), RefusalReason.AccessDenied);
        AssertRefused(Check(fs, Root + @"\missing.tmp"), RefusalReason.NotFound);
        FilePolicy p = NewPolicy();
        AssertRefused(p.Evaluate(FileFacts.Missing(Root + @"\x.tmp", ProbeError.SharingViolation)), RefusalReason.InUse);
        AssertRefused(p.Evaluate(FileFacts.Missing(Root + @"\x.tmp", ProbeError.Other)), RefusalReason.ProbeFailed);
        AssertRefused(p.Evaluate(FileFacts.Missing(Root + @"\x.tmp", ProbeError.PathTooLong)), RefusalReason.InvalidPath);
    }

    [Test]
    public void Limits_can_only_get_stricter()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NewPolicy(minAge: TimeSpan.FromDays(13)));
        Assert.Throws<ArgumentOutOfRangeException>(() => NewPolicy(minAge: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => NewPolicy(minAge: TimeSpan.FromDays(-30)));
        Assert.Throws<ArgumentOutOfRangeException>(() => NewPolicy(maxBytes: (256L * 1024 * 1024) + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => NewPolicy(maxBytes: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => NewPolicy(maxBytes: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => NewPolicy(maxBytes: long.MaxValue));

        var fs = new FakeFileSystem();
        fs.AddFile(Root + @"\mid.tmp", 2 * 1024 * 1024, Now.AddDays(-20), Now.AddDays(-20));
        fs.AddFile(Root + @"\small.tmp", 100, Now.AddDays(-40), Now.AddDays(-40));
        FilePolicy strict = NewPolicy(TimeSpan.FromDays(30), 1024 * 1024);
        AssertRefused(Check(fs, Root + @"\mid.tmp", strict), RefusalReason.TooNew);
        Assert.True(Check(fs, Root + @"\small.tmp", strict).Eligible);
    }

    [Test]
    public void Refuses_unsafe_temp_roots_at_construction()
    {
        foreach (string bad in new[] { @"C:\", @"C:\Temp", @"\\server\share\x\y", @"relative\x\y", @"C:\a\b:ads", string.Empty })
        {
            Assert.Throws<ArgumentException>(() => new FilePolicy(bad, new FixedClock(Now)), bad);
        }
    }

    [Test]
    public void Age_in_days_is_never_negative()
    {
        var fs = new FakeFileSystem();
        FileFacts f = fs.AddFile(Root + @"\a.tmp", 10, Old, Now.AddDays(-15));
        Assert.Equal(15, NewPolicy().AgeDays(f));
        FileFacts future = f with { CreationTimeUtc = Now.AddDays(5), LastWriteTimeUtc = Now.AddDays(5) };
        Assert.Equal(0, NewPolicy().AgeDays(future));
    }
}

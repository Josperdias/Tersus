using System.Runtime.Versioning;
using Tersus.Core.Advice;
using Tersus.Core.Apps;
using Tersus.Core.Duplicates;
using Tersus.Core.IO;
using Tersus.Core.Scanning;
using Tersus.Core.Windows;
using Tersus.Tests.Framework;
using Tersus.Tests.Support;

namespace Tersus.Tests;

[WindowsOnly]
[SupportedOSPlatform("windows")]
[Category("windows")]
public class WindowsMiscTests
{
    [Test]
    public void The_registry_app_catalog_lists_programs_read_only()
    {
        IReadOnlyList<InstalledApp> apps = new RegistryAppCatalog().List();
        Assert.True(apps.Count > 0, "a Windows machine should have at least one registered program");
        Assert.True(apps.All(a => !string.IsNullOrWhiteSpace(a.Name)));
        Assert.Equal(apps.Count, apps.Select(a => (a.Name.ToUpperInvariant(), a.Version, a.Publisher?.ToUpperInvariant())).Distinct().Count(), "32/64-bit duplicates must be merged");
        Assert.True(apps.Zip(apps.Skip(1)).All(p => string.Compare(p.First.Name, p.Second.Name, StringComparison.CurrentCultureIgnoreCase) <= 0), "sorted by name");
    }

    [Test]
    public void Windows_facts_are_collected_without_changing_anything()
    {
        AdvisorInput input = WindowsFacts.Collect([]);
        Assert.True(input.VolumeTotalBytes is > 0);
        Assert.True(input.VolumeFreeBytes is >= 0);
        Assert.True(input.RecycleBinBytes is null or >= 0);
        IReadOnlyList<Recommendation> advice = SystemAdvisor.Build(input);
        Assert.True(advice.Count >= 2);
    }

    [Test]
    public void The_scanner_survives_real_system_folders_including_inaccessible_ones()
    {
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        ScanResult r = new StorageScanner().Scan(programData);
        Assert.True(r.Completed);
        Assert.True(r.TotalBytes >= 0 && r.FileCount >= 0);
        Assert.True(r.Tree.Bytes == r.TotalBytes);
    }

    [Test]
    public void The_scanner_counts_a_folder_it_is_not_allowed_to_read()
    {
        using var sb = new WinSandbox();
        sb.Old("ok/file.tmp");
        string locked = sb.Path_("locked");
        Directory.CreateDirectory(locked);
        WinSandbox.CreateAt(Path.Combine(locked, "secret.bin"), new byte[50]);
        sb.DenyCurrentUser(locked);
        ScanResult r = new StorageScanner().Scan(sb.Root);
        Assert.True(r.Completed);
        Assert.Equal(1L, r.InaccessibleFolders, "the denied folder must be reported");
        Assert.True(r.InaccessibleSamples.Any(s => s.EndsWith("locked", StringComparison.OrdinalIgnoreCase)));
        sb.AllowAgain(locked);
    }

    [Test]
    public void The_scanner_does_not_follow_junctions_or_symlinks_on_windows()
    {
        using var sb = new WinSandbox();
        string outside = sb.NewOutsideFolder();
        WinSandbox.CreateAt(Path.Combine(outside, "huge.bin"), new byte[200_000], 10);
        sb.Old("real/file.bin");
        bool j = sb.TryJunction("junction", outside);
        bool s = sb.TrySymlinkDirectory("symdir", outside);
        bool sf = sb.TrySymlinkFile("symfile.bin", Path.Combine(outside, "huge.bin"));
        Assert.SkipIf(!j && !s && !sf, "Cannot create links here.");
        ScanResult r = new StorageScanner().Scan(sb.Root);
        Assert.Equal(8L, r.TotalBytes, "only the real 8-byte file counts: " + r.TotalBytes);
        Assert.True(r.LinksSkipped >= 1);
    }

    [Test]
    public void The_duplicate_finder_ignores_hard_links_using_real_file_identities()
    {
        using var sb = new WinSandbox();
        byte[] data = new byte[40_000];
        new Random(3).NextBytes(data);
        string a = sb.Create("a.bin", data);
        sb.HardLink(a, sb.Path_("a-hardlink.bin"));
        sb.Create("copy.bin", data);
        var fs = new WindowsFileSystem();
        DuplicateResult r = new DuplicateAnalyzer(p => fs.Probe(p).Identity).Find(sb.Root, new DuplicateOptions { MinFileBytes = 1024 });
        Assert.Equal(1, r.Groups.Count);
        Assert.Equal(2, r.Groups[0].Files.Count, "the hard link must not count as a second copy");
        Assert.Equal(1L, r.Stats.HardLinksIgnored);
    }

    [Test]
    public void Cleanup_services_pick_the_current_users_temp_folder()
    {
        using var sb = new WinSandbox();
        CleanupServices.Stack stack = CleanupServices.Create(new Tersus.Core.Storage.AppPaths(sb.NewDataFolder()));
        Assert.True(stack.Policy is not null, stack.Problem);
        string expected = sb.Canonical(Path.GetTempPath()).TrimEnd('\\');
        Assert.True(string.Equals(stack.Policy!.TempRoot, expected, StringComparison.OrdinalIgnoreCase), $"{stack.Policy.TempRoot} vs {expected}");
    }
}

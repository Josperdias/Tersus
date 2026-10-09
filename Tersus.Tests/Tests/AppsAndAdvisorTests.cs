using Tersus.Core.Advice;
using Tersus.Core.Apps;
using Tersus.Tests.Framework;

namespace Tersus.Tests;

[Category("apps")]
public class AppsAndAdvisorTests
{
    private static Dictionary<string, object?> Entry(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);

    [Test]
    public void Parses_a_typical_uninstall_entry()
    {
        InstalledApp? app = UninstallEntryParser.Parse(
            Entry(("DisplayName", "  Blender 4.2  "), ("Publisher", "Blender Foundation"), ("DisplayVersion", "4.2.1"), ("InstallDate", "20260115"),
                ("EstimatedSize", 1_500_000), ("InstallLocation", @"C:\Program Files\Blender Foundation\Blender 4.2"), ("UninstallString", "MsiExec.exe /X{...}")),
            "HKLM 64-bit");
        Assert.NotNull(app);
        Assert.Equal("Blender 4.2", app!.Name);
        Assert.Equal("Blender Foundation", app.Publisher);
        Assert.Equal("4.2.1", app.Version);
        Assert.Equal(new DateTime(2026, 1, 15), app.InstallDate);
        Assert.Equal(1_500_000L * 1024, app.EstimatedSizeBytes);
        Assert.True(app.HasUninstaller);
        Assert.Equal("HKLM 64-bit", app.Source);
    }

    [Test]
    public void Hides_system_components_updates_and_nameless_entries()
    {
        Assert.Null(UninstallEntryParser.Parse(Entry(("DisplayName", "Hidden thing"), ("SystemComponent", 1)), "x"));
        Assert.Null(UninstallEntryParser.Parse(Entry(("DisplayName", "Patch"), ("ParentKeyName", "Office")), "x"));
        Assert.Null(UninstallEntryParser.Parse(Entry(("DisplayName", "Security Update for X"), ("ReleaseType", "Security Update")), "x"));
        Assert.Null(UninstallEntryParser.Parse(Entry(("DisplayName", "Hotfix 1"), ("ReleaseType", "Hotfix")), "x"));
        Assert.Null(UninstallEntryParser.Parse(Entry(("DisplayName", "KB5001234")), "x"));
        Assert.Null(UninstallEntryParser.Parse(Entry(("Publisher", "No name")), "x"));
        Assert.Null(UninstallEntryParser.Parse(Entry(("DisplayName", "   ")), "x"));
        Assert.NotNull(UninstallEntryParser.Parse(Entry(("DisplayName", "KB Home Planner")), "x"));
    }

    [Test]
    public void Never_invents_missing_or_invalid_data()
    {
        InstalledApp app = UninstallEntryParser.Parse(Entry(("DisplayName", "Tool"), ("InstallDate", "garbage"), ("EstimatedSize", 0), ("Publisher", "  ")), "x")!;
        Assert.Null(app.InstallDate);
        Assert.Null(app.EstimatedSizeBytes);
        Assert.Null(app.Publisher);
        Assert.Null(app.Version);
        Assert.False(app.HasUninstaller);
        Assert.Null(UninstallEntryParser.Parse(Entry(("DisplayName", "T"), ("InstallDate", "18990101")), "x")!.InstallDate);
        Assert.Null(UninstallEntryParser.Parse(Entry(("DisplayName", "T"), ("InstallDate", "20261340")), "x")!.InstallDate);
        Assert.Equal(5L * 1024, UninstallEntryParser.Parse(Entry(("DisplayName", "T"), ("EstimatedSize", 5u)), "x")!.EstimatedSizeBytes);
    }

    [Test]
    public void Merges_the_32_and_64_bit_views_and_sorts_by_name()
    {
        var a = new InstalledApp("Zeta", "P", "1", null, 10, null, "HKLM 64-bit", true);
        var b = new InstalledApp("zeta", "p", "1", null, 20, null, "HKLM 32-bit", true);
        var c = new InstalledApp("Alpha", null, null, null, null, null, "HKCU", false);
        IReadOnlyList<InstalledApp> merged = UninstallEntryParser.MergeAndSort([a, b, c]);
        Assert.Equal(2, merged.Count);
        Assert.Equal("Alpha", merged[0].Name);
        Assert.Equal(20L, merged[1].EstimatedSizeBytes);
    }

    private static AdvisorInput Input(long total = 1000L * 1024 * 1024 * 1024, long free = 500L * 1024 * 1024 * 1024) =>
        new() { VolumeTotalBytes = total, VolumeFreeBytes = free, VolumeName = "C:" };

    [Test]
    public void Free_space_thresholds_drive_severity()
    {
        long gb = 1024L * 1024 * 1024;
        Assert.Equal(AdviceSeverity.Important, SystemAdvisor.Build(Input(100 * gb, 5 * gb)).Single(r => r.Id == "low-space").Severity);
        Assert.Equal(AdviceSeverity.Attention, SystemAdvisor.Build(Input(100 * gb, 15 * gb)).Single(r => r.Id == "low-space").Severity);
        Assert.False(SystemAdvisor.Build(Input(100 * gb, 50 * gb)).Any(r => r.Id == "low-space"));
        Assert.False(SystemAdvisor.Build(new AdvisorInput()).Any(r => r.Id == "low-space"));
    }

    [Test]
    public void Recycle_bin_advice_makes_clear_that_tersus_does_not_empty_it()
    {
        Recommendation r = SystemAdvisor.Build(Input() with { RecycleBinBytes = 3L * 1024 * 1024 * 1024 }).Single(x => x.Id == "recycle-bin");
        Assert.Contains(r.Detail, "não libera espaço");
        Assert.Contains(r.Caution!, "nunca esvazia");
        Assert.False(SystemAdvisor.Build(Input() with { RecycleBinBytes = 1024 }).Any(x => x.Id == "recycle-bin"), "tiny bins are not worth a warning");
    }

    [Test]
    public void Hibernation_and_pagefile_advice_explains_but_never_acts()
    {
        var advice = SystemAdvisor.Build(Input() with { HibernationFileBytes = 6L << 30, PageFileBytes = 4L << 30, WindowsOldExists = true });
        Recommendation h = advice.Single(x => x.Id == "hibernation");
        Assert.Contains(h.HowTo!, "powercfg /h off");
        Assert.Contains(h.Caution!, "não altera");
        Assert.True(advice.Any(x => x.Id == "pagefile"));
        Assert.True(advice.Any(x => x.Id == "windows-old"));
        Assert.False(SystemAdvisor.Build(Input()).Any(x => x.Id == "hibernation"));
    }

    [Test]
    public void Lists_at_most_five_big_apps_and_labels_sizes_as_reported()
    {
        long gb = 1L << 30;
        var apps = Enumerable.Range(1, 8).Select(i => new InstalledApp($"App{i}", null, null, null, (i + 1) * gb, null, "x", true)).ToList();
        Recommendation r = SystemAdvisor.Build(Input() with { Apps = apps }).Single(x => x.Id == "big-apps");
        Assert.Equal(5, r.Evidence!.Split(';').Length);
        Assert.True(r.Evidence.StartsWith("App8", StringComparison.Ordinal));
        Assert.Contains(r.Caution!, "informou");
        Assert.False(SystemAdvisor.Build(Input() with { Apps = [new InstalledApp("Small", null, null, null, 5 << 20, null, "x", true)] }).Any(x => x.Id == "big-apps"));
    }

    [Test]
    public void Browser_and_blender_advice_promises_not_to_touch_them()
    {
        var advice = SystemAdvisor.Build(Input() with { BrowsersDetected = ["Google Chrome", "Microsoft Edge"], BlenderDetected = true });
        Assert.Contains(advice.Single(x => x.Id == "browsers").Caution!, "nunca mexe");
        Assert.Contains(advice.Single(x => x.Id == "blender").Caution!, "nunca oferece");
        Assert.True(advice.Any(x => x.Id == "storage-sense"));
        Assert.True(advice.Any(x => x.Id == "appdata"));
    }

    [Test]
    public void Cloud_only_files_are_explained_as_not_using_local_space()
    {
        Recommendation r = SystemAdvisor.Build(Input() with { CloudOnlyBytes = 12L << 30 }).Single(x => x.Id == "cloud");
        Assert.Contains(r.Detail, "não ocupam espaço local");
    }

    [Test]
    public void Every_recommendation_has_text_and_a_unique_id()
    {
        long gb = 1L << 30;
        var all = SystemAdvisor.Build(Input(100 * gb, 5 * gb) with
        {
            RecycleBinBytes = 2 * gb, HibernationFileBytes = gb, PageFileBytes = gb, WindowsOldExists = true, DownloadsBytes = 5 * gb,
            CloudOnlyBytes = gb, BrowsersDetected = ["Chrome"], BlenderDetected = true,
            Apps = [new InstalledApp("Big", null, null, null, 3 * gb, null, "x", true)],
        });
        Assert.Equal(all.Count, all.Select(r => r.Id).Distinct().Count());
        Assert.True(all.All(r => r.Title.Length > 0 && r.Detail.Length > 0));
        Assert.True(all.Count >= 10);
    }
}

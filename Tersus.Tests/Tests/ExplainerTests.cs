using Tersus.Core.Knowledge;
using Tersus.Tests.Framework;

namespace Tersus.Tests;

[Category("knowledge")]
public class ExplainerTests
{
    private static Explanation File(string path) => FileExplainer.Explain(path, isDirectory: false);

    private static Explanation Dir(string path) => FileExplainer.Explain(path, isDirectory: true);

    [Test]
    public void System_files_are_protected_with_high_confidence()
    {
        foreach (string p in new[] { @"C:\pagefile.sys", @"C:\hiberfil.sys", @"C:\swapfile.sys", @"C:\Windows\System32\kernel32.dll", @"C:\Windows\WinSxS\x\y.dll", @"C:\Windows\Installer\abc.msi",
            @"C:\$Recycle.Bin\S-1-5\$R123.txt", @"C:\System Volume Information\x", @"C:\Program Files\Tool\tool.exe", @"C:\Program Files (x86)\Tool\a.dll" })
        {
            Explanation e = File(p);
            Assert.Equal(RiskLevel.Protected, e.Risk, p);
            Assert.True(e.Confidence == Confidence.High, p);
        }
    }

    [Test]
    public void Browser_profiles_are_protected_and_never_offered_for_cleaning()
    {
        foreach (string p in new[]
        {
            @"C:\Users\u\AppData\Local\Google\Chrome\User Data\Default\Cache\f_001",
            @"C:\Users\u\AppData\Local\Microsoft\Edge\User Data\Profile 1",
            @"C:\Users\u\AppData\Roaming\Mozilla\Firefox\Profiles\abc.default\cookies.sqlite",
            @"C:\Users\u\AppData\Local\BraveSoftware\Brave-Browser\User Data",
        })
        {
            Explanation e = Dir(p);
            Assert.Equal(RiskLevel.Protected, e.Risk, p);
            Assert.Contains(e.Recommendation, "navegador", p);
        }
    }

    [Test]
    public void Blender_projects_are_protected_and_autosave_copies_need_caution()
    {
        Assert.Equal(RiskLevel.Protected, File(@"D:\Arte\cena.blend").Risk);
        Assert.Equal(RiskLevel.Caution, File(@"D:\Arte\cena.blend1").Risk);
        Assert.Equal(RiskLevel.Protected, Dir(@"C:\Users\u\AppData\Roaming\Blender Foundation\Blender\4.2").Risk);
    }

    [Test]
    public void Personal_folders_are_protected_even_for_unknown_files()
    {
        Assert.Equal(RiskLevel.Protected, File(@"C:\Users\u\Documents\tese.docx").Risk);
        Assert.Equal(RiskLevel.Protected, File(@"C:\Users\u\Pictures\ferias.heic").Risk);
        Assert.Equal(RiskLevel.Protected, File(@"C:\Users\u\Downloads\algo.xyz").Risk);
        Assert.Equal(RiskLevel.Protected, Dir(@"C:\Users\u\Desktop\Projeto").Risk);
    }

    [Test]
    public void Temp_files_are_low_risk_but_the_advice_points_to_the_safe_tool()
    {
        Explanation tmp = File(@"C:\Users\u\AppData\Local\Temp\abc.tmp");
        Assert.Equal(RiskLevel.Low, tmp.Risk);
        Assert.Equal("Arquivo temporário", tmp.Title);
        Assert.Contains(tmp.Recommendation, "Lixeira");
        Assert.Contains(tmp.Recommendation, "14");
        Explanation other = File(@"C:\Users\u\AppData\Local\Temp\data.bin");
        Assert.Equal(RiskLevel.Low, other.Risk);
        Assert.Equal("Pasta temporária do usuário", other.Title);
    }

    [Test]
    public void Developer_folders_have_specific_honest_explanations()
    {
        Assert.Equal(RiskLevel.Low, Dir(@"D:\proj\web\node_modules").Risk);
        Assert.Equal(RiskLevel.Protected, Dir(@"D:\proj\web\.git").Risk);
        Assert.Equal(RiskLevel.Low, Dir(@"D:\proj\__pycache__").Risk);
        Assert.Equal(Confidence.Low, Dir(@"D:\proj\bin").Confidence);
    }

    [Test]
    public void Virtual_disks_and_databases_are_never_described_as_disposable()
    {
        Assert.Equal(RiskLevel.Protected, File(@"D:\vm\disk.vhdx").Risk);
        Assert.Equal(RiskLevel.Protected, File(@"C:\Users\u\AppData\Local\Packages\CanonicalGroupLimited.Ubuntu\LocalState\ext4.vhdx").Risk);
        Assert.Equal(RiskLevel.Protected, File(@"D:\mail\arquivo.pst").Risk);
        Assert.Equal(RiskLevel.Caution, File(@"D:\x\backup.bak").Risk);
        Assert.Equal(RiskLevel.Caution, File(@"D:\x\app.sqlite").Risk);
    }

    [Test]
    public void Unknown_things_are_reported_as_unknown_with_low_confidence()
    {
        Explanation f = File(@"D:\misc\thing.zzzz");
        Assert.Equal(Confidence.Low, f.Confidence);
        Assert.Equal(RiskLevel.Info, f.Risk);
        Assert.Contains(f.WhatItIs, "não reconhece");
        Explanation d = Dir(@"D:\misc\whatever");
        Assert.Equal(Confidence.Low, d.Confidence);
        Assert.Contains(d.Consequence, "tamanho grande");
    }

    [Test]
    public void Paths_with_forward_slashes_or_odd_case_are_understood()
    {
        Assert.Equal(RiskLevel.Protected, File("c:/WINDOWS/system32/cmd.exe").Risk);
        Assert.Equal(RiskLevel.Protected, File(@"C:\USERS\U\APPDATA\LOCAL\GOOGLE\CHROME\USER DATA\x").Risk);
    }

    [Test]
    public void Explanations_are_complete_and_sensitive_locations_are_never_called_low_risk()
    {
        string[] segments = ["Windows", "System32", "Program Files", "Users", "u", "Documents", "AppData", "Local", "Roaming", "Temp", "Google", "Chrome", "User Data",
            "node_modules", "bin", "x.tmp", "a.log", "b.blend", "c.vhdx", "d.dll", "ProgramData", "OneDrive", "Pictures", "e.docx", "f.zzz", "$Recycle.Bin"];
        var rng = new Random(1234);
        for (int i = 0; i < 20_000; i++)
        {
            string path = "C:\\" + string.Join("\\", Enumerable.Range(0, rng.Next(1, 7)).Select(_ => segments[rng.Next(segments.Length)]));
            bool dir = rng.Next(2) == 0;
            Explanation e = FileExplainer.Explain(path, dir);
            Assert.True(e.Title.Length > 0 && e.WhatItIs.Length > 0 && e.Consequence.Length > 0 && e.Recommendation.Length > 0 && e.Origin.Length > 0, path);
            if (ProtectedLocations.IsOperatingSystemOrProgram(path) || ProtectedLocations.IsBrowserProfile(path))
            {
                Assert.True(e.Risk is RiskLevel.Protected or RiskLevel.Caution, $"{path} is in a protected location but was called {e.Risk}");
            }
        }
    }

    [Test]
    public void File_categories_cover_common_types_case_insensitively()
    {
        Assert.Equal(FileCategory.Video, FileCategories.Of(".MP4"));
        Assert.Equal(FileCategory.Image, FileCategories.Of("jpg"));
        Assert.Equal(FileCategory.DiskImage, FileCategories.Of(".vhdx"));
        Assert.Equal(FileCategory.Temporary, FileCategories.Of(".tmp"));
        Assert.Equal(FileCategory.Other, FileCategories.Of(".unknownext"));
        Assert.Equal("Vídeos", FileCategories.Label(FileCategory.Video));
    }

    [Test]
    public void Protected_location_helpers_are_whole_folder_and_case_insensitive()
    {
        Assert.True(ProtectedLocations.IsOperatingSystemOrProgram(@"c:\WINDOWS\x"));
        Assert.False(ProtectedLocations.IsOperatingSystemOrProgram(@"C:\WindowsApps\x"), "a folder that merely starts with 'Windows' is not the Windows folder");
        Assert.True(ProtectedLocations.IsPersonalData(@"C:\Users\u\Documents\a"));
        Assert.False(ProtectedLocations.IsPersonalData(@"C:\Users\u\MyDocumentsBackup\a"));
        Assert.True(ProtectedLocations.IsAppData(@"C:\Users\u\AppData\Local\x"));
        Assert.True(ProtectedLocations.IsSensitive(@"C:\Users\u\AppData\Local\Temp\a.tmp"));
        Assert.False(ProtectedLocations.IsSensitive(@"D:\Videos\clip.mp4".Replace("Videos", "Clips")));
    }
}

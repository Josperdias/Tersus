using Tersus.Core.Policy;
using Tersus.Tests.Framework;
using Tersus.Tests.Support;

namespace Tersus.Tests;

[Category("policy")]
public class TempRootResolverTests
{
    private const string Profile = @"C:\Users\tester";
    private const string Temp = @"C:\Users\tester\AppData\Local\Temp";

    private static readonly string[] Protected =
    [
        @"C:\Users\tester\Documents", @"C:\Users\tester\Desktop", @"C:\Users\tester\Downloads",
        @"C:\Users\tester\Pictures", @"C:\Users\tester\Music", @"C:\Users\tester\Videos",
        @"C:\Users\tester\OneDrive", @"C:\Windows", @"C:\Program Files", @"C:\Program Files (x86)", @"C:\ProgramData",
    ];

    private static FakeFileSystem Typical()
    {
        var fs = new FakeFileSystem();
        fs.AddDirectory(Profile);
        fs.AddDirectory(Temp);
        foreach (string p in Protected)
        {
            fs.AddDirectory(p);
        }

        return fs;
    }

    [Test]
    public void Accepts_the_typical_per_user_temp_folder()
    {
        TempRootResult r = TempRootResolver.Resolve(Typical(), Temp + @"\", Profile, Protected);
        Assert.True(r.IsSafe, r.Problem);
        Assert.Equal(Temp, r.CanonicalRoot);
    }

    [Test]
    public void Canonicalises_8dot3_aliases_through_the_handle()
    {
        FakeFileSystem fs = Typical();
        fs.AddDirectory(@"C:\Users\TESTER~1", finalPath: Profile);
        fs.AddDirectory(@"C:\Users\TESTER~1\AppData\Local\Temp", finalPath: Temp);
        TempRootResult r = TempRootResolver.Resolve(fs, @"C:\Users\TESTER~1\AppData\Local\Temp", @"C:\Users\TESTER~1", Protected);
        Assert.True(r.IsSafe, r.Problem);
        Assert.Equal(Temp, r.CanonicalRoot);
    }

    [Test]
    public void Refuses_a_temp_that_is_a_link_to_somewhere_outside_the_profile()
    {
        FakeFileSystem fs = Typical();
        fs.AddDirectory(Temp, finalPath: @"D:\Elsewhere\Temp");
        TempRootResult r = TempRootResolver.Resolve(fs, Temp, Profile, Protected);
        Assert.False(r.IsSafe);
    }

    [Test]
    public void Refuses_temp_equal_to_drive_root_or_profile_or_outside_the_profile()
    {
        FakeFileSystem fs = Typical();
        fs.AddDirectory(@"C:\");
        fs.AddDirectory(@"D:\Temp\x\y");
        Assert.False(TempRootResolver.Resolve(fs, @"C:\", Profile, Protected).IsSafe);
        Assert.False(TempRootResolver.Resolve(fs, Profile, Profile, Protected).IsSafe);
        Assert.False(TempRootResolver.Resolve(fs, @"D:\Temp\x\y", Profile, Protected).IsSafe);
        Assert.False(TempRootResolver.Resolve(fs, @"C:\Windows\Temp", Profile, Protected).IsSafe);
    }

    [Test]
    public void Refuses_temp_that_contains_or_sits_inside_a_protected_folder()
    {
        FakeFileSystem fs = Typical();
        fs.AddDirectory(@"C:\Users\tester\AppData");
        fs.AddDirectory(@"C:\Users\tester\AppData\Local");
        fs.AddDirectory(@"C:\Users\tester\Documents\Temp");
        Assert.False(TempRootResolver.Resolve(fs, @"C:\Users\tester\Documents", Profile, Protected).IsSafe, "equals Documents");
        Assert.False(TempRootResolver.Resolve(fs, @"C:\Users\tester\Documents\Temp", Profile, Protected).IsSafe, "inside Documents");
        string[] withDeepProtected = [.. Protected, @"C:\Users\tester\AppData\Local\Temp\KeepMe"];
        fs.AddDirectory(@"C:\Users\tester\AppData\Local\Temp\KeepMe");
        Assert.False(TempRootResolver.Resolve(fs, Temp, Profile, withDeepProtected).IsSafe, "contains a protected folder");
    }

    [Test]
    public void Refuses_missing_or_non_directory_roots_and_bad_syntax()
    {
        FakeFileSystem fs = Typical();
        fs.AddFile(@"C:\Users\tester\AppData\Local\notadir", 1, DateTime.UtcNow, DateTime.UtcNow);
        Assert.False(TempRootResolver.Resolve(fs, @"C:\Users\tester\AppData\Local\Nope\x", Profile, Protected).IsSafe);
        Assert.False(TempRootResolver.Resolve(fs, @"C:\Users\tester\AppData\Local\notadir", Profile, Protected).IsSafe);
        Assert.False(TempRootResolver.Resolve(fs, @"\\server\share\Temp\x", Profile, Protected).IsSafe);
        Assert.False(TempRootResolver.Resolve(fs, Temp, @"\\server\profile", Protected).IsSafe);
        Assert.False(TempRootResolver.Resolve(fs, string.Empty, Profile, Protected).IsSafe);
    }
}

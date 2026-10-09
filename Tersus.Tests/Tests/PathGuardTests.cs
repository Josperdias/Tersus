using Tersus.Core.Policy;
using Tersus.Tests.Framework;

namespace Tersus.Tests;

[Category("policy")]
public class PathGuardTests
{
    private static void Bad(string? input, PathProblem expected)
    {
        bool ok = PathGuard.TryNormalize(input, out string normalized, out PathProblem problem);
        Assert.False(ok, $"'{input}' should be rejected but normalised to '{normalized}'");
        Assert.Equal(expected, problem, $"wrong problem for '{input}'");
        Assert.Equal(string.Empty, normalized);
    }

    private static string Good(string input)
    {
        Assert.True(PathGuard.TryNormalize(input, out string normalized, out PathProblem problem), $"'{input}' rejected: {problem}");
        return normalized;
    }

    [Test]
    public void Normalizes_separators_drive_case_and_dot_segments()
    {
        Assert.Equal(@"C:\Users\tester\AppData\Local\Temp\a.tmp", Good(@"c:/Users//tester/./AppData/../AppData/Local/Temp/a.tmp"));
        Assert.Equal(@"C:\a\b", Good(@"C:\a\b\"));
        Assert.Equal(@"C:\", Good(@"C:\"));
        Assert.Equal(@"C:\a.tmp", Good(@"C:\..\..\a.tmp"));
    }

    [Test]
    public void Rejects_empty_and_relative_paths()
    {
        Bad(null, PathProblem.Empty);
        Bad(string.Empty, PathProblem.Empty);
        Bad("   ", PathProblem.Empty);
        Bad(@"relative\a.tmp", PathProblem.NotAbsoluteDrivePath);
        Bad("a.tmp", PathProblem.NotAbsoluteDrivePath);
        Bad(@"C:a.tmp", PathProblem.NotAbsoluteDrivePath);
        Bad("C:", PathProblem.NotAbsoluteDrivePath);
        Bad(@"\Windows\a.tmp", PathProblem.NotAbsoluteDrivePath);
        Bad(@"1:\a.tmp", PathProblem.NotAbsoluteDrivePath);
    }

    [Test]
    public void Rejects_unc_and_device_paths()
    {
        Bad(@"\\server\share\a.tmp", PathProblem.NotAbsoluteDrivePath);
        Bad("//server/share/a.tmp", PathProblem.NotAbsoluteDrivePath);
        Bad(@"\\?\C:\x.tmp", PathProblem.DevicePrefix);
        Bad(@"\\.\C:\x.tmp", PathProblem.DevicePrefix);
        Bad("//?/C:/x.tmp", PathProblem.DevicePrefix);
        Bad(@"\\?\UNC\server\share\x.tmp", PathProblem.DevicePrefix);
    }

    [Test]
    public void Rejects_alternate_data_streams()
    {
        Bad(@"C:\a\b.tmp:Zone.Identifier", PathProblem.AlternateStream);
        Bad(@"C:\a:b\c.tmp", PathProblem.AlternateStream);
        Bad(@"C:\a\b.tmp::$DATA", PathProblem.AlternateStream);
    }

    [Test]
    public void Rejects_trailing_dots_and_spaces_that_win32_would_strip()
    {
        Bad(@"C:\a\b.tmp.", PathProblem.TrailingDotOrSpace);
        Bad(@"C:\a\b.tmp ", PathProblem.TrailingDotOrSpace);
        Bad(@"C:\a\...\b.tmp", PathProblem.TrailingDotOrSpace);
        Bad(@"C:\a \b.tmp", PathProblem.TrailingDotOrSpace);
    }

    [Test]
    public void Rejects_reserved_device_names_with_or_without_extension()
    {
        Bad(@"C:\a\CON", PathProblem.ReservedName);
        Bad(@"C:\a\nul.tmp", PathProblem.ReservedName);
        Bad(@"C:\a\COM1.txt", PathProblem.ReservedName);
        Bad(@"C:\a\LPT9", PathProblem.ReservedName);
        Bad(@"C:\CONOUT$", PathProblem.ReservedName);
        Bad("C:\\a\\COM\u00B9.tmp", PathProblem.ReservedName);
        Bad(@"C:\a\con .txt", PathProblem.ReservedName);
        Assert.Equal(@"C:\a\console.tmp", Good(@"C:\a\console.tmp"));
        Assert.Equal(@"C:\a\COM10.tmp", Good(@"C:\a\COM10.tmp"));
        Assert.Equal(@"C:\a\nul2.tmp", Good(@"C:\a\nul2.tmp"));
    }

    [Test]
    public void Rejects_invalid_characters_and_control_codes()
    {
        foreach (char c in "<>\"|?*")
        {
            Bad($"C:\\a\\x{c}y.tmp", PathProblem.InvalidCharacter);
        }

        Bad("C:\\a\\\u0001.tmp", PathProblem.InvalidCharacter);
        Bad("C:\\a\\b\0.tmp", PathProblem.InvalidCharacter);
        Bad("C:\\a\\b\n.tmp", PathProblem.InvalidCharacter);
    }

    [Test]
    public void Enforces_the_259_character_limit_exactly()
    {
        string prefix = @"C:\" + new string('d', 100) + @"\";
        string ok = prefix + new string('f', 259 - prefix.Length - 4) + ".tmp";
        Assert.Equal(259, ok.Length);
        Assert.Equal(ok, Good(ok));
        Bad(ok + "x", PathProblem.TooLong);
    }

    [Test]
    public void IsStrictlyUnder_is_component_wise_and_case_insensitive()
    {
        const string root = @"C:\Users\t\Temp";
        Assert.True(PathGuard.IsStrictlyUnder(root, @"C:\Users\t\Temp\a.tmp"));
        Assert.True(PathGuard.IsStrictlyUnder(root, @"C:\USERS\T\TEMP\sub\A.TMP"));
        Assert.False(PathGuard.IsStrictlyUnder(root, root), "the root itself is not 'inside' the root");
        Assert.False(PathGuard.IsStrictlyUnder(root, @"C:\Users\t\Temp2\a.tmp"), "sibling sharing a prefix");
        Assert.False(PathGuard.IsStrictlyUnder(root, @"C:\Users\t\Temp.tmp"));
        Assert.False(PathGuard.IsStrictlyUnder(root, @"C:\Users\t"));
        Assert.False(PathGuard.IsStrictlyUnder(root, @"D:\Users\t\Temp\a.tmp"));
    }

    [Test]
    public void Extension_is_taken_from_the_last_dot()
    {
        Assert.Equal(".tmp", PathGuard.ExtensionOf("a.tmp"));
        Assert.Equal(".exe", PathGuard.ExtensionOf("a.tmp.exe"));
        Assert.Equal(".tmp", PathGuard.ExtensionOf("report.docx.tmp"));
        Assert.Equal(string.Empty, PathGuard.ExtensionOf("noext"));
        Assert.Equal(".tmp", PathGuard.ExtensionOf(".tmp"));
    }

    [Test]
    public void Strips_extended_length_prefix()
    {
        Assert.Equal(@"C:\x", PathGuard.StripExtendedPrefix(@"\\?\C:\x"));
        Assert.Equal(@"\\srv\sh", PathGuard.StripExtendedPrefix(@"\\?\UNC\srv\sh"));
        Assert.Equal(@"C:\x", PathGuard.StripExtendedPrefix(@"C:\x"));
    }

    [Test]
    public void Depth_counts_components_below_the_drive_root()
    {
        Assert.Equal(0, PathGuard.Depth(@"C:\"));
        Assert.Equal(1, PathGuard.Depth(@"C:\a"));
        Assert.Equal(3, PathGuard.Depth(@"C:\a\b\c"));
    }
}

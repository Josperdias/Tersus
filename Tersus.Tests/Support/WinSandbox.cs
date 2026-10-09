using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Tersus.Core.IO;
using Tersus.Core.Policy;
using Tersus.Core.Storage;
using Tersus.Core.Windows;

namespace Tersus.Tests.Support;

[SupportedOSPlatform("windows")]
internal static class TestNative
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateHardLinkW(string newFileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint bufferSize);
}

/// <summary>
/// A scratch folder INSIDE the real TEMP folder (so the production policy genuinely applies) plus helpers to create the nasty things
/// the safety checks must survive: junctions, symbolic links, hard links, locks, deny-ACLs. Only used on Windows CI/VMs, only touches
/// files it created itself.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WinSandbox : IDisposable
{
    private readonly List<string> _links = [];
    private readonly List<(string Path, FileSystemAccessRule Rule)> _denyRules = [];
    private readonly List<string> _extraDirs = [];
    private readonly WindowsFileSystem _fs = new();

    public WinSandbox()
    {
        string raw = Path.Combine(Path.GetTempPath(), "Tersus-it-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(raw);
        Root = Canonical(raw);
    }

    /// <summary>Canonical (long-name, link-free) path of the sandbox.</summary>
    public string Root { get; }

    public string Path_(string relative) => System.IO.Path.Combine(Root, relative);

    public string Canonical(string path)
    {
        FileFacts f = _fs.Probe(path);
        return f.FinalPath is null ? path : PathGuard.StripExtendedPrefix(f.FinalPath);
    }

    /// <summary>A folder next to the sandbox but OUTSIDE TEMP (under LocalAppData) - the "victim" area that must never be touched.</summary>
    public string NewOutsideFolder()
    {
        string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tersus-it-outside-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        _extraDirs.Add(dir);
        return Canonical(dir);
    }

    public string NewDataFolder()
    {
        string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tersus-it-data-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        _extraDirs.Add(dir);
        return dir;
    }

    public string Old(string relative, string content = "conteudo", int ageDays = 30) => Create(relative, Encoding.UTF8.GetBytes(content), ageDays);

    public string Create(string relative, byte[] content, int ageDays = 30) => CreateAt(Path_(relative), content, ageDays);

    public static string CreateAt(string fullPath, byte[] content, int ageDays = 30)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, content);
        DateTime t = DateTime.UtcNow.AddDays(-ageDays);
        File.SetCreationTimeUtc(fullPath, t);
        File.SetLastWriteTimeUtc(fullPath, t);
        return fullPath;
    }

    public void SetAttributes(string path, FileAttributes attributes) => File.SetAttributes(path, attributes);

    public bool TryJunction(string linkRelative, string target)
    {
        string link = Path_(linkRelative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(link)!);
        var psi = new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using Process p = Process.Start(psi)!;
        p.WaitForExit(20_000);
        if (p.ExitCode == 0 && Directory.Exists(link))
        {
            _links.Add(link);
            return true;
        }

        return false;
    }

    public bool TrySymlinkFile(string linkRelative, string target)
    {
        try
        {
            string link = Path_(linkRelative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(link)!);
            File.CreateSymbolicLink(link, target);
            _links.Add(link);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public bool TrySymlinkDirectory(string linkRelative, string target)
    {
        try
        {
            string link = Path_(linkRelative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(link)!);
            Directory.CreateSymbolicLink(link, target);
            _links.Add(link);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public void HardLink(string existing, string newLink)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(newLink)!);
        if (!TestNative.CreateHardLinkW(newLink, existing, IntPtr.Zero))
        {
            throw new IOException("CreateHardLink failed: " + Marshal.GetLastWin32Error());
        }
    }

    public string ShortName(string longPath)
    {
        var sb = new StringBuilder(520);
        uint n = TestNative.GetShortPathNameW(longPath, sb, (uint)sb.Capacity);
        return n == 0 ? longPath : sb.ToString();
    }

    /// <summary>Denies the current user read/list/delete on a file or folder until the sandbox is disposed.</summary>
    public void DenyCurrentUser(string path)
    {
        SecurityIdentifier sid = WindowsIdentity.GetCurrent().User!;
        FileSystemRights rights = FileSystemRights.Read | FileSystemRights.ReadAndExecute | FileSystemRights.Delete | FileSystemRights.ListDirectory;
        var rule = new FileSystemAccessRule(sid, rights, AccessControlType.Deny);
        if (Directory.Exists(path))
        {
            var di = new DirectoryInfo(path);
            DirectorySecurity sec = di.GetAccessControl();
            sec.AddAccessRule(rule);
            di.SetAccessControl(sec);
        }
        else
        {
            var fi = new FileInfo(path);
            FileSecurity sec = fi.GetAccessControl();
            sec.AddAccessRule(rule);
            fi.SetAccessControl(sec);
        }

        _denyRules.Add((path, rule));
    }

    public void AllowAgain(string path)
    {
        foreach ((string p, FileSystemAccessRule rule) in _denyRules.Where(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            RemoveRule(p, rule);
            _denyRules.Remove((p, rule));
        }
    }

    public void Dispose()
    {
        foreach ((string path, FileSystemAccessRule rule) in _denyRules)
        {
            try
            {
                RemoveRule(path, rule);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
            }
        }

        // Remove links first (non-recursive Delete removes only the link, never the target).
        foreach (string link in _links)
        {
            try
            {
                if (Directory.Exists(link))
                {
                    Directory.Delete(link, recursive: false);
                }
                else if (File.Exists(link))
                {
                    File.Delete(link);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        foreach (string dir in new[] { Root }.Concat(_extraDirs))
        {
            DeleteTree(dir);
        }
    }

    private static void RemoveRule(string path, FileSystemAccessRule rule)
    {
        if (Directory.Exists(path))
        {
            var di = new DirectoryInfo(path);
            DirectorySecurity sec = di.GetAccessControl();
            sec.RemoveAccessRuleSpecific(rule);
            di.SetAccessControl(sec);
        }
        else if (File.Exists(path))
        {
            var fi = new FileInfo(path);
            FileSecurity sec = fi.GetAccessControl();
            sec.RemoveAccessRuleSpecific(rule);
            fi.SetAccessControl(sec);
        }
    }

    private static void DeleteTree(string dir)
    {
        try
        {
            if (!Directory.Exists(dir))
            {
                return;
            }

            foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(f, FileAttributes.Normal);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the sandbox lives in TEMP and is harmless.
        }
    }

    public static string HashOf(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>Independent proof (does not use the product's own verification): looks for a Recycle Bin record of this exact path.</summary>
    public static bool BinHasEntryFor(string originalPath)
    {
        string? root = System.IO.Path.GetPathRoot(originalPath);
        string? sid = WindowsIdentity.GetCurrent().User?.Value;
        if (root is null || sid is null)
        {
            return false;
        }

        string dir = System.IO.Path.Combine(root, "$Recycle.Bin", sid);
        if (!Directory.Exists(dir))
        {
            return false;
        }

        foreach (string info in Directory.EnumerateFiles(dir, "$I*"))
        {
            try
            {
                if (RecycleBinInfoFile.TryParse(File.ReadAllBytes(info), out string recorded, out _, out _)
                    && string.Equals(recorded, originalPath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return false;
    }

    public static int BinEntryCount()
    {
        string? sid = WindowsIdentity.GetCurrent().User?.Value;
        string root = System.IO.Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";
        string dir = System.IO.Path.Combine(root, "$Recycle.Bin", sid ?? "none");
        return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "$I*").Count() : 0;
    }

    public static CleanupServices.Stack Services(out string dataFolder, WinSandbox sandbox)
    {
        dataFolder = sandbox.NewDataFolder();
        CleanupServices.Stack stack = CleanupServices.Create(new AppPaths(dataFolder));
        return stack;
    }
}

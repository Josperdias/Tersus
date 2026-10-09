using System.Text.RegularExpressions;
using Tersus.Tests.Framework;
using Tersus.Tests.Support;

namespace Tersus.Tests;

/// <summary>
/// Static security audit of the PRODUCT source (Tersus.Core and Tersus.App). It fails the build when someone adds an API that can
/// delete, overwrite, move, kill, reconfigure, phone home or auto-start - anywhere but the single, reviewed place that is allowed to.
/// The engine itself is tested below (mutation tests) so a regression in the audit is caught too.
/// </summary>
[Category("static-audit")]
[Category("security")]
public class StaticAuditTests
{
    private sealed record Rule(string Id, string Why, Regex Pattern, string[] AllowedIn);

    private static readonly string[] None = [];
    private const string AppData = "Tersus.Core/Storage/AppDataFiles.cs";

    private static Regex R(string pattern) => new(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Rule[] CodeRules =
    [
        new("no-delete", "Deleting files/folders is only allowed inside AppDataFiles (own data folder, path-guarded).", R(@"\b(?:File|Directory|FileSystem)\s*\.\s*Delete\w*\s*\(|\.\s*Delete\s*\(|\bDeleteFile\w*\b|\bRemoveDirectory\w*\b|\bNtDeleteFile\b|\bFileDispositionInfo\b|FILE_DELETE_ON_CLOSE|FileOptions\s*\.\s*DeleteOnClose"), [AppData]),
        new("no-move-copy", "Moving/copying files is only allowed inside AppDataFiles (atomic save and quarantine).", R(@"\b(?:File|Directory)\s*\.\s*(?:Move|Copy|Replace)\s*\(|\.\s*(?:MoveTo|CopyTo)\s*\(|\bMoveFileEx\w*\b|\bCopyFile\w*\b"), [AppData]),
        new("no-write", "Writing/creating files is only allowed inside AppDataFiles (history, logs, self-test file).", R(@"\bFile\s*\.\s*(?:Create\w*|WriteAll\w*|AppendAll\w*|AppendText|OpenWrite|SetAttributes|SetLastWrite\w*|SetCreation\w*|SetLastAccess\w*|SetUnixFileMode|SetAccessControl|Encrypt|Decrypt)\s*\(|\bnew\s+StreamWriter\b|\bFileMode\s*\.\s*(?:Create|CreateNew|Append|Truncate|OpenOrCreate)\b|\bFileAccess\s*\.\s*(?:Write|ReadWrite)\b|\bDirectory\s*\.\s*(?:CreateDirectory|SetCreation\w*|SetLastWrite\w*)\s*\("), [AppData, "Tersus.Core/Storage/AppPaths.cs"]),
        new("no-registry-write", "The registry is read-only for Tersus.", R(@"\.\s*(?:SetValue|CreateSubKey|DeleteSubKey\w*|DeleteValue)\s*\(|\bRegistry\s*\.\s*SetValue\b|OpenSubKey\s*\([^)]*,\s*(?:writable\s*:\s*)?true|\bRegistryKeyPermissionCheck\b|\bRegSetValue\w*\b|\bRegCreateKey\w*\b|\bRegDeleteKey\w*\b|\bRegDeleteValue\w*\b"), None),
        new("no-process-control", "Tersus never starts, kills or scripts other programs, except the small reviewed ShellLauncher (Explorer / Settings).", R(@"\bProcess\s*\.\s*(?:Start|Kill)\b|\bnew\s+ProcessStartInfo\b|\.\s*Kill\s*\(|\bTerminateProcess\b|\bShellExecute\w*\b|\bCreateProcess\w*\b|\bWScript\b"), ["Tersus.App/ShellLauncher.cs"]),
        new("no-network", "Tersus is offline by design: no HTTP, sockets, DNS or web views.", R(@"\bSystem\s*\.\s*Net\b|\bHttpClient\b|\bWebClient\b|\bWebRequest\b|\bHttpWebRequest\b|\bTcpClient\b|\bUdpClient\b|\bTcpListener\b|\bSocket\b|\bDns\b|\bWebSocket\w*\b|\bWebBrowser\b|\bWebView2?\b|\bNetworkInformation\b"), None),
        new("no-empty-recycle-bin", "Tersus never empties the Recycle Bin.", R(@"\bSHEmptyRecycleBin\w*\b|\bEmptyRecycleBin\b"), None),
        new("shell-delete-only-in-recycler", "The shell delete call exists only in the Recycle Bin implementation.", R(@"\bSHFileOperation\w*\b|\bFoDelete\b|\bIFileOperation\b|\bFileOperation\b"), ["Tersus.Core/Windows/NativeMethods.cs", "Tersus.Core/Windows/ShellRecycler.cs"]),
        new("pinvoke-only-in-nativemethods", "P/Invoke declarations live in one reviewed file.", R(@"\bDllImport\b|\bLibraryImport\b|\bGetProcAddress\b|\bLoadLibrary\w*\b|\bGetDelegateForFunctionPointer\b|\bNativeLibrary\b"), ["Tersus.Core/Windows/NativeMethods.cs"]),
        new("no-dynamic-code", "No dynamic code loading or generation.", R(@"\bAssembly\s*\.\s*(?:Load\w*|UnsafeLoad\w*)\b|\bAppDomain\b|\bReflection\s*\.\s*Emit\b|\bDynamicMethod\b|\bCSharpScript\b|\bCSharpCodeProvider\b|\bExpression\s*\.\s*Compile\b"), None),
        new("no-autostart-services-tasks", "No auto-start, services or scheduled tasks.", R(@"\bSpecialFolder\s*\.\s*(?:Startup|CommonStartup)\b|\bServiceController\b|\bServiceBase\b|\bTaskScheduler\b|\bRegisterApplicationRestart\b|\bBackgroundService\b|\bIHostedService\b|\bStartupTask\b"), None),
        new("no-telemetry", "No telemetry or crash-report uploads.", R(@"\bApplicationInsights\b|\bTelemetryClient\b|\bSentry\w*\b|\bAppCenter\b|\bFirebase\w*\b|\bGoogleAnalytics\b"), None),
        new("no-unsafe-code", "No unsafe code or raw memory games in a tool that decides what gets deleted.", R(@"\bunsafe\b|\bstackalloc\b|\bMarshal\s*\.\s*(?:Copy|WriteInt\w*|WriteByte|AllocHGlobal)\b"), None),
    ];

    private static readonly string[] AllowedPInvokeEntryPoints =
    [
        "CreateFileW", "GetFileInformationByHandle", "GetFileInformationByHandleEx", "GetFinalPathNameByHandleW",
        "GetCompressedFileSizeW", "GetVolumeNameForVolumeMountPointW", "SHFileOperationW", "SHQueryRecycleBinW",
    ];

    private static List<string> Violations(string relPath, string source, IEnumerable<Rule> rules)
    {
        string code = SourceScanner.StripCommentsAndStrings(source);
        var found = new List<string>();
        foreach (Rule rule in rules)
        {
            if (rule.AllowedIn.Contains(relPath, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (Match m in rule.Pattern.Matches(code))
            {
                found.Add($"{relPath}:{SourceScanner.LineOf(code, m.Index)}: [{rule.Id}] '{m.Value.Trim()}' - {rule.Why}");
            }
        }

        return found;
    }

    private static IEnumerable<(string Rel, string Source)> ProductFiles()
    {
        string root = SourceScanner.RepoRoot();
        foreach (string f in SourceScanner.ProductSourceFiles(root))
        {
            yield return (Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllText(f));
        }
    }

    [Test]
    public void Product_code_contains_no_forbidden_apis()
    {
        var all = new List<string>();
        int files = 0;
        foreach ((string rel, string src) in ProductFiles())
        {
            files++;
            all.AddRange(Violations(rel, src, CodeRules));
        }

        Assert.True(files >= 20, $"the audit looked at only {files} files; is the repository layout what it expects?");
        Assert.True(all.Count == 0, "Static audit failures:\n  " + string.Join("\n  ", all));
    }

    [Test]
    public void Only_the_allow_listed_native_functions_are_imported()
    {
        string root = SourceScanner.RepoRoot();
        string native = File.ReadAllText(Path.Combine(root, "Tersus.Core/Windows/NativeMethods.cs"));
        var imported = Regex.Matches(native, @"EntryPoint\s*=\s*""(?<n>[A-Za-z0-9_]+)""").Select(m => m.Groups["n"].Value).ToList();
        int attributes = Regex.Matches(SourceScanner.StripCommentsAndStrings(native), @"\bDllImport\b").Count;
        Assert.Equal(attributes, imported.Count, "every DllImport must name its EntryPoint explicitly so it can be audited");
        foreach (string name in imported)
        {
            Assert.Contains(AllowedPInvokeEntryPoints, name, $"unexpected native function '{name}'; add it only after a security review");
        }

        Assert.Equal(AllowedPInvokeEntryPoints.Length, imported.Distinct().Count(), "the allow-list and the imports must match exactly");
    }

    [Test]
    public void The_only_caller_of_Recycle_is_the_executor_and_the_recycler_itself()
    {
        var offenders = new List<string>();
        foreach ((string rel, string src) in ProductFiles())
        {
            if (rel is "Tersus.Core/Cleanup/CleanupExecutor.cs" or "Tersus.Core/Windows/ShellRecycler.cs" or "Tersus.Core/Cleanup/IRecycler.cs")
            {
                continue;
            }

            string code = SourceScanner.StripCommentsAndStrings(src);
            foreach (Match m in Regex.Matches(code, @"\.\s*Recycle\s*\("))
            {
                offenders.Add($"{rel}:{SourceScanner.LineOf(code, m.Index)}");
            }
        }

        Assert.True(offenders.Count == 0, "IRecycler.Recycle may only be called by the policy-gated executor: " + string.Join(", ", offenders));
    }

    [Test]
    public void The_recycler_keeps_its_safety_flags_and_proof_requirement()
    {
        string root = SourceScanner.RepoRoot();
        string code = SourceScanner.StripCommentsAndStrings(File.ReadAllText(Path.Combine(root, "Tersus.Core/Windows/ShellRecycler.cs")));
        Assert.Contains(code, "NativeMethods.FofAllowUndo", "the shell delete must always carry FOF_ALLOWUNDO (recycle, do not destroy)");
        Assert.Contains(code, "NativeMethods.FofWantNukeWarning", "the shell must be told to warn instead of silently deleting for good");
        Assert.Contains(code, "RecycleStatus.GoneWithoutProof", "a file that vanishes without a bin record must be reported");
        Assert.Contains(code, "FindBinEntry", "proof that the file reached the bin is mandatory");
        Assert.Contains(code, "RecycleBinSettings.Read", "the bin configuration must be checked before moving anything");
        Assert.Contains(code, "RunSelfTest", "the bin must be proven with a self-test file first");
    }

    [Test]
    public void The_executor_keeps_every_revalidation_step()
    {
        string root = SourceScanner.RepoRoot();
        string code = SourceScanner.StripCommentsAndStrings(File.ReadAllText(Path.Combine(root, "Tersus.Core/Cleanup/CleanupExecutor.cs")));
        foreach (string needle in new[] { "confirmation.Matches(plan)", "recycler.Preflight(", "fs.TryHold(", "policy.Evaluate(facts)", "CleanupPlanner.ChangedSinceAnalysis(", "GoneWithoutProof", "cancellationToken.IsCancellationRequested" })
        {
            Assert.Contains(code, needle, $"CleanupExecutor must still contain '{needle}'");
        }
    }

    [Test]
    public void The_policy_constants_have_not_been_widened()
    {
        string root = SourceScanner.RepoRoot();
        string code = SourceScanner.StripCommentsAndStrings(File.ReadAllText(Path.Combine(root, "Tersus.Core/Policy/FilePolicy.cs")));
        string raw = File.ReadAllText(Path.Combine(root, "Tersus.Core/Policy/FilePolicy.cs"));
        Assert.Contains(code, "HardMaxFileBytes = 256L * 1024 * 1024");
        Assert.Contains(code, "HardMinAge = TimeSpan.FromDays(14)");
        Assert.Contains(raw, "AllowedExtensions = [\".tmp\", \".temp\"]");
        Assert.False(Regex.IsMatch(raw, @"AllowedExtensions\s*=\s*\[[^\]]*,[^\]]*,"), "only two extensions may ever be allowed");
    }

    [Test]
    public void No_project_uses_third_party_packages_or_trimming()
    {
        string root = SourceScanner.RepoRoot();
        foreach (string csproj in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories).Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            string text = File.ReadAllText(csproj);
            Assert.False(text.Contains("<PackageReference", StringComparison.Ordinal), $"{Path.GetFileName(csproj)} references a NuGet package; Tersus has zero third-party dependencies by design");
            Assert.False(Regex.IsMatch(text, @"<PublishTrimmed>\s*true", RegexOptions.IgnoreCase), $"{Path.GetFileName(csproj)} enables trimming; WPF is not trim-safe");
        }
    }

    [Test]
    public void The_interface_contains_no_web_content_and_no_destructive_commands()
    {
        string root = SourceScanner.RepoRoot();
        string app = Path.Combine(root, "Tersus.App");
        if (!Directory.Exists(app))
        {
            Assert.Skip("The WPF project is not part of this checkout.");
            return;
        }

        foreach (string xaml in Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(xaml);
            Assert.False(Regex.IsMatch(text, @"<\s*(WebBrowser|WebView2?)\b"), $"{Path.GetFileName(xaml)} embeds web content");
            Assert.False(Regex.IsMatch(text, @"(Source|NavigateUri)\s*=\s*""https?://"), $"{Path.GetFileName(xaml)} references the internet");
        }
    }

    // ---- tests of the audit engine itself --------------------------------------------------------------------------------

    [Test]
    public void Engine_flags_real_violations()
    {
        string[] bad =
        [
            "File.Delete(path);", "Directory.Delete(dir, true);", "new FileInfo(p).Delete();", "FileSystem.DeleteFile(p, UIOption.OnlyErrorDialogs, RecycleOption.DeletePermanently);",
            "File.Move(a, b);", "File.WriteAllText(p, \"x\");", "using var w = new StreamWriter(p);", "var m = FileMode.Create;", "File.SetAttributes(p, FileAttributes.Normal);",
            "key.SetValue(\"a\", 1);", "Registry.CurrentUser.CreateSubKey(\"x\");", "k.OpenSubKey(\"x\", true);", "k.OpenSubKey(\"x\", writable: true);", "k.DeleteSubKeyTree(\"x\");",
            "Process.Start(\"cmd\");", "var psi = new ProcessStartInfo();", "p.Kill();", "using var c = new HttpClient();", "new WebClient();", "var s = new Socket(a,b,c);",
            "SHEmptyRecycleBin(h, null, 0);", "SHFileOperationW(ref op);", "[DllImport(\"x.dll\")] static extern void F();", "Assembly.LoadFrom(p);", "Environment.GetFolderPath(Environment.SpecialFolder.Startup);",
            "unsafe { }", "System.Net.Dns.GetHostName();",
        ];
        foreach (string snippet in bad)
        {
            Assert.True(Violations("Some/File.cs", snippet, CodeRules).Count > 0, "the audit failed to flag: " + snippet);
        }
    }

    [Test]
    public void Engine_ignores_comments_strings_and_look_alikes()
    {
        string[] fine =
        [
            "// File.Delete(path);", "/* File.Delete(path); */", "var s = \"File.Delete(path)\";", "var v = @\"Process.Start(\"\"x\"\")\";", "var t = $\"HttpClient {x}\";",
            "var r = \"\"\"\nFile.Delete(x)\n\"\"\";", "var c = '\"'; var d = 1;", "text.Replace(\"a\", \"b\");", "list.Remove(x); dict.Remove(k);", "x.Deleted = true;",
            "var f = File.ReadAllText(p); var b = File.ReadAllBytes(p);", "File.Exists(p); Directory.Exists(p); Directory.EnumerateFiles(p);", "using var fs = new FileStream(p, new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read });",
            "k.OpenSubKey(\"x\", writable: false);", "k.OpenSubKey(\"x\");", "ProcessorCount", "var socketCount = 1;", "Environment.SpecialFolder.LocalApplicationData",
        ];
        foreach (string snippet in fine)
        {
            List<string> v = Violations("Some/File.cs", snippet, CodeRules);
            Assert.True(v.Count == 0, $"false positive on: {snippet}\n  {string.Join("\n  ", v)}");
        }
    }

    [Test]
    public void Allow_lists_are_respected_per_file_and_nowhere_else()
    {
        Assert.Equal(0, Violations(AppData, "File.Delete(p); File.Move(a,b); File.WriteAllText(p,\"\");", CodeRules).Count);
        Assert.True(Violations("Tersus.Core/History/HistoryStore.cs", "File.Delete(p);", CodeRules).Count > 0);
        Assert.Equal(0, Violations("Tersus.Core/Windows/ShellRecycler.cs", "SHFileOperationW(ref op);", CodeRules).Count);
        Assert.True(Violations("Tersus.Core/Cleanup/CleanupExecutor.cs", "SHFileOperationW(ref op);", CodeRules).Count > 0);
        Assert.Equal(0, Violations("Tersus.App/ShellLauncher.cs", "Process.Start(psi);", CodeRules).Count);
        Assert.True(Violations("Tersus.App/MainWindow.xaml.cs", "Process.Start(psi);", CodeRules).Count > 0);
    }

    [Test]
    public void The_stripper_keeps_line_numbers_and_length()
    {
        string src = "a // x\n/* y\nz */ b\n\"s\\\"t\" c\n@\"q\"\"r\" d\n'\\'' e\n";
        string stripped = SourceScanner.StripCommentsAndStrings(src);
        Assert.Equal(src.Length, stripped.Length);
        Assert.Equal(src.Count(ch => ch == '\n'), stripped.Count(ch => ch == '\n'));
        Assert.Contains(stripped, "a ");
        Assert.Contains(stripped, " b");
        Assert.Contains(stripped, " c");
        Assert.Contains(stripped, " d");
        Assert.Contains(stripped, " e");
        Assert.False(stripped.Contains('x') || stripped.Contains('y') || stripped.Contains('z') || stripped.Contains('q') || stripped.Contains('r'));
    }
}

using Tersus.Core.IO;
using Tersus.Core.Policy;
using Tersus.Tests.Framework;
using Tersus.Tests.Support;

namespace Tersus.Tests;

/// <summary>
/// Property tests with a fixed seed: whatever the input, "eligible" must imply every hard invariant of the policy.
/// These are the tests that would catch a clever path or an unexpected attribute combination slipping through.
/// </summary>
[Category("policy")]
[Category("security")]
public class PolicyFuzzTests
{
    private const string Root = @"C:\Users\tester\AppData\Local\Temp";
    private static readonly DateTime Now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    private static readonly string[] Tokens =
    [
        @"C:", "c:", "D:", @"\", "/", "\\", "..", ".", "Users", "tester", "AppData", "Local", "Temp", "TEMP", "Temp2", "a", "b",
        "x.tmp", "y.temp", ".tmp", "CON", "nul.tmp", "z.tmp.", "z.tmp ", "w:s", "\u00FC", "~1", "PROGRA~1", ":", "?", "*", "\u0001",
        "x.TMP", "file.tmp.exe", "file.docx.tmp", "\u00B9", "...", " ", "$Recycle.Bin", "Windows", "Documents",
    ];

    [Test]
    public void Random_paths_are_eligible_only_when_every_invariant_holds()
    {
        var policy = new FilePolicy(Root, new FixedClock(Now));
        var rng = new Random(20261009);
        int eligible = 0;
        for (int i = 0; i < 100_000; i++)
        {
            // Half of the inputs start inside TEMP so the interesting rules are actually reached.
            string prefix = rng.Next(2) == 0 ? Root + @"\" : string.Empty;
            string path = prefix + string.Concat(Enumerable.Range(0, rng.Next(1, 9)).Select(_ => Tokens[rng.Next(Tokens.Length)] + (rng.Next(3) == 0 ? @"\" : string.Empty)));
            PolicyDecision d = policy.EvaluateName(path);
            if (!d.Eligible)
            {
                continue;
            }

            eligible++;
            string n = d.NormalizedPath!;
            Assert.True(n.StartsWith(Root + @"\", StringComparison.OrdinalIgnoreCase), $"not inside TEMP: '{path}' -> '{n}'");
            Assert.True(n.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".temp", StringComparison.OrdinalIgnoreCase), $"bad extension: '{n}'");
            Assert.True(n.Length <= 259, $"too long: '{n}'");
            Assert.False(n.IndexOf(':', 2) >= 0, $"stream marker: '{n}'");
            Assert.False(n.Contains(@"\\", StringComparison.Ordinal), $"double separator: '{n}'");
            Assert.False(n.Contains(@"\..\", StringComparison.Ordinal) || n.EndsWith(@"\..", StringComparison.Ordinal), $"dot-dot: '{n}'");
            Assert.False(n[^1] == '.' || n[^1] == ' ', $"trailing dot/space: '{n}'");
            Assert.False(n.Any(c => c < 0x20 || "<>\"|?*".Contains(c, StringComparison.Ordinal)), $"illegal char: '{n}'");
            foreach (string comp in n[3..].Split('\\'))
            {
                Assert.False(comp.Length == 0 || comp.EndsWith('.') || comp.EndsWith(' '), $"bad component in '{n}'");
            }
        }

        Assert.True(eligible > 100, $"fuzzer never reached the accept path ({eligible} hits); the test would be vacuous");
    }

    [Test]
    public void Random_file_facts_are_eligible_only_when_every_invariant_holds()
    {
        var policy = new FilePolicy(Root, new FixedClock(Now));
        var rng = new Random(424242);
        FileAttributes[] attrs =
        [
            0, FileAttributes.Archive, FileAttributes.Hidden, FileAttributes.System, FileAttributes.ReadOnly, FileAttributes.Directory,
            FileAttributes.ReparsePoint, FileAttributes.Offline, FileAttributes.Temporary, FileAttributes.Device, (FileAttributes)0x00400000, (FileAttributes)0x00040000,
            FileAttributes.Archive | FileAttributes.ReparsePoint, FileAttributes.Hidden | FileAttributes.Archive,
        ];
        string[] finals = [Root + @"\f.tmp", Root + @"\other.tmp", @"C:\Users\tester\Documents\f.tmp", @"\\?\" + Root + @"\f.tmp", Root.ToUpperInvariant() + @"\F.TMP", string.Empty];
        int eligible = 0;
        for (int i = 0; i < 100_000; i++)
        {
            int ageDays = rng.Next(-5, 60);
            var facts = new FileFacts
            {
                Path = rng.Next(4) == 0 ? Root + @"\g.tmp" : Root + @"\f.tmp",
                Exists = rng.Next(10) != 0,
                IsDirectory = rng.Next(12) == 0,
                Attributes = attrs[rng.Next(attrs.Length)],
                Length = rng.Next(5) switch { 0 => 0, 1 => policy.MaxFileBytes, 2 => policy.MaxFileBytes + 1, 3 => -1, _ => rng.NextInt64(0, 400L * 1024 * 1024) },
                CreationTimeUtc = Now.AddDays(-ageDays).AddMinutes(rng.Next(-10, 10)),
                LastWriteTimeUtc = Now.AddDays(-rng.Next(-5, 60)),
                HardLinkCount = rng.Next(5) switch { 0 => null, 1 => 2u, 2 => 0u, _ => 1u },
                Identity = rng.Next(10) == 0 ? null : new FileIdentity(1, (ulong)i, 0),
                FinalPath = rng.Next(8) == 0 ? null : finals[rng.Next(finals.Length)],
                IsDeep = rng.Next(8) != 0,
            };

            PolicyDecision d = policy.Evaluate(facts);
            if (!d.Eligible)
            {
                continue;
            }

            eligible++;
            Assert.True(facts.Exists && !facts.IsDirectory && !facts.IsReparsePoint, "must be an existing regular non-link file");
            Assert.True((facts.Attributes & (FileAttributes.System | FileAttributes.ReadOnly | FileAttributes.Offline | FileAttributes.Device | FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0, $"unsafe attributes {facts.Attributes}");
            Assert.True(((uint)facts.Attributes & 0x00440000u) == 0, "cloud recall attributes");
            Assert.InRange(facts.Length, 0, policy.MaxFileBytes);
            Assert.True(Now - facts.CreationTimeUtc >= policy.MinAge && Now - facts.LastWriteTimeUtc >= policy.MinAge, "age rule");
            Assert.True(facts.IsDeep && facts.HardLinkCount == 1 && facts.Identity is not null && facts.FinalPath is not null, "deep facts / single link");
            Assert.True(PathGuard.AreSame(PathGuard.StripExtendedPrefix(facts.FinalPath!), facts.Path), "final path must equal the path");
        }

        Assert.True(eligible > 100, $"fuzzer never reached the accept path ({eligible} hits)");
    }
}

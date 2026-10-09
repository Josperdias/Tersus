using System.Globalization;

namespace Tersus.App.Smoke;

/// <summary>
/// Command-line switches of the automated interface check used by the build pipeline. Nothing here is reachable from the normal UI.
/// <c>--smoke-test OUTDIR</c> turns the mode on (OUTDIR becomes the data folder, so the real user data is never touched).
/// </summary>
internal sealed record SmokeOptions(
    string OutDir,
    string ScanFolder,
    string? CleanupFolder,
    bool Execute,
    int? ExpectEligible,
    int? ExpectMoved,
    string? BigScanFolder,
    bool ExpectInaccessible)
{
    public static SmokeOptions? Parse(IReadOnlyList<string> args)
    {
        string? outDir = null;
        string? scan = null;
        string? cleanup = null;
        bool execute = false;
        int? expectEligible = null;
        int? expectMoved = null;
        string? bigScan = null;
        bool expectInaccessible = false;
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            string? Next() => i + 1 < args.Count ? args[++i] : null;
            switch (a)
            {
                case "--smoke-test":
                    outDir = Next();
                    break;
                case "--smoke-scan":
                    scan = Next();
                    break;
                case "--smoke-cleanup-folder":
                    cleanup = Next();
                    break;
                case "--smoke-execute":
                    execute = true;
                    break;
                case "--smoke-expect-eligible":
                    expectEligible = int.TryParse(Next(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int e) ? e : null;
                    break;
                case "--smoke-big-scan":
                    bigScan = Next();
                    break;
                case "--smoke-expect-inaccessible":
                    expectInaccessible = true;
                    break;
                case "--smoke-expect-moved":
                    expectMoved = int.TryParse(Next(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int m) ? m : null;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(outDir))
        {
            return null;
        }

        return new SmokeOptions(
            Path.GetFullPath(outDir),
            Path.GetFullPath(string.IsNullOrWhiteSpace(scan) ? AppContext.BaseDirectory : scan),
            string.IsNullOrWhiteSpace(cleanup) ? null : Path.GetFullPath(cleanup),
            execute,
            expectEligible,
            expectMoved,
            string.IsNullOrWhiteSpace(bigScan) ? null : Path.GetFullPath(bigScan),
            expectInaccessible);
    }
}

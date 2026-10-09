using Tersus.Core;
using Tersus.Core.Cleanup;
using Tersus.Core.IO;
using Tersus.Core.Policy;

namespace Tersus.Tests.Support;

public sealed class MemoryLog : ICleanupLog
{
    public List<string> Lines { get; } = [];

    public string? Path => "memory";

    public void Append(string line) => Lines.Add(line);
}

public sealed class FakeRecycler(FakeFileSystem fs) : IRecycler
{
    public bool PreflightReady { get; set; } = true;

    public string PreflightMessage { get; set; } = "Lixeira simulada pronta.";

    public long? Capacity { get; set; }

    public int PreflightCalls { get; private set; }

    public List<string> RecycleCalls { get; } = [];

    public List<(string Path, long Length)> Bin { get; } = [];

    public HashSet<string> FailPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> UnprovenPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> ThrowPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Action<string>? BeforeRecycle { get; set; }

    public RecyclerPreflight Preflight(string tempRoot)
    {
        PreflightCalls++;
        return new RecyclerPreflight(PreflightReady, PreflightMessage, Capacity);
    }

    public RecycleOutcome Recycle(string path, long expectedLength)
    {
        RecycleCalls.Add(path);
        BeforeRecycle?.Invoke(path);
        if (ThrowPaths.Contains(path))
        {
            throw new IOException("simulated crash inside the shell call");
        }

        if (FailPaths.Contains(path))
        {
            return new RecycleOutcome(RecycleStatus.Failed, "falha simulada");
        }

        if (UnprovenPaths.Contains(path))
        {
            fs.Remove(path);
            return new RecycleOutcome(RecycleStatus.GoneWithoutProof, "sumiu sem prova");
        }

        fs.Remove(path);
        Bin.Add((path, expectedLength));
        return new RecycleOutcome(RecycleStatus.Recycled, "Enviado à Lixeira (simulado).");
    }
}

/// <summary>A fake TEMP folder, policy, recycler and log wired together for planner/executor tests.</summary>
public sealed class CleanupFixture
{
    public const string Root = @"C:\Users\tester\AppData\Local\Temp";

    public static readonly DateTime Now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    public CleanupFixture()
    {
        Fs.AddDirectory(@"C:\Users");
        Fs.AddDirectory(@"C:\Users\tester");
        Fs.AddDirectory(@"C:\Users\tester\AppData");
        Fs.AddDirectory(@"C:\Users\tester\AppData\Local");
        Fs.AddDirectory(Root);
        Clock = new FixedClock(Now);
        Policy = new FilePolicy(Root, Clock);
        Recycler = new FakeRecycler(Fs);
    }

    public FakeFileSystem Fs { get; } = new();

    public FixedClock Clock { get; }

    public FilePolicy Policy { get; }

    public FakeRecycler Recycler { get; }

    public MemoryLog Log { get; } = new();

    public string AddOld(string relative, long length = 1000, int ageDays = 30, FileAttributes attrs = FileAttributes.Archive, uint links = 1)
    {
        string full = Root + @"\" + relative;
        EnsureParents(full);
        DateTime t = Now.AddDays(-ageDays);
        Fs.AddFile(full, length, t, t, attrs, links);
        return full;
    }

    public string AddFolder(string relative, FileAttributes extra = 0, string? finalPath = null)
    {
        string full = Root + @"\" + relative;
        EnsureParents(full);
        Fs.AddDirectory(full, finalPath, extra);
        return full;
    }

    public CandidateScanResult Scan(CancellationToken ct = default, int max = 50_000) =>
        new CleanupCandidateScanner(Policy, Fs).Scan(null, ct, max);

    public CleanupPlanner Planner => new(Policy, Fs);

    public CleanupExecutor NewExecutor() => new(Policy, Fs, Recycler, Log);

    public CleanupPlan PlanAll() => Planner.Simulate(Scan().Candidates);

    public ExecutionConfirmation Confirm(CleanupPlan plan) => ExecutionConfirmation.For(plan, Now);

    private void EnsureParents(string full)
    {
        string dir = full[..full.LastIndexOf('\\')];
        if (dir.Length <= Root.Length || Fs.Exists(dir))
        {
            return;
        }

        EnsureParents(dir);
        Fs.AddDirectory(dir);
    }
}

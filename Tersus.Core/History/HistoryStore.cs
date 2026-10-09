using System.Text.Json;
using System.Text.Json.Serialization;
using Tersus.Core.Scanning;
using Tersus.Core.Storage;

namespace Tersus.Core.History;

public sealed record FolderStat(string Name, long Bytes);

/// <summary>
/// A compact summary of one completed analysis: totals and first-level folder sizes only. It deliberately holds no file names and
/// no file contents. It lives in %LOCALAPPDATA%\Tersus\historico.json and never leaves the computer.
/// </summary>
public sealed record Snapshot(
    Guid Id,
    DateTime CreatedUtc,
    string Root,
    long TotalBytes,
    long FileCount,
    long? VolumeUsedBytes,
    IReadOnlyList<FolderStat> Folders,
    IReadOnlyList<FolderStat> Extensions);

public enum DeltaKind
{
    Same,
    Grew,
    Shrunk,
    New,
    Removed,
}

public sealed record FolderDelta(string Name, long BeforeBytes, long AfterBytes, DeltaKind Kind)
{
    public long Delta => AfterBytes - BeforeBytes;
}

public sealed record SnapshotComparison(Snapshot Before, Snapshot After, IReadOnlyList<FolderDelta> Folders)
{
    public long TotalDelta => After.TotalBytes - Before.TotalBytes;

    public TimeSpan Elapsed => After.CreatedUtc - Before.CreatedUtc;
}

/// <summary>Up to 24 local snapshots, newest last. Tolerates a damaged file (moves it aside and starts again) and writes atomically.</summary>
public sealed class HistoryStore(AppDataFiles files, int maxSnapshots = HistoryStore.DefaultMaxSnapshots)
{
    public const int DefaultMaxSnapshots = 24;
    public const int MaxFoldersPerSnapshot = 200;
    public const int MaxExtensionsPerSnapshot = 20;
    private const long MaxFileBytes = 4L * 1024 * 1024;
    private const int Schema = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();

    private sealed class FileModel
    {
        public int Schema { get; set; }

        public List<Snapshot> Snapshots { get; set; } = [];
    }

    public IReadOnlyList<Snapshot> Load()
    {
        lock (_gate)
        {
            return LoadUnlocked();
        }
    }

    /// <summary>Stores a summary of a COMPLETED scan (partial scans would make comparisons misleading). Returns null if nothing was stored.</summary>
    public Snapshot? Record(ScanResult result, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Completed)
        {
            return null;
        }

        var folders = result.Tree.Children.Take(MaxFoldersPerSnapshot).Select(c => new FolderStat(c.Name, c.Bytes)).ToList();
        if (result.Tree.DirectFilesBytes > 0)
        {
            folders.Add(new FolderStat("(arquivos soltos na raiz)", result.Tree.DirectFilesBytes));
        }

        var snapshot = new Snapshot(
            Guid.NewGuid(),
            nowUtc,
            result.Root,
            result.TotalBytes,
            result.FileCount,
            result.VolumeTotalBytes is not null && result.VolumeFreeBytes is not null ? result.VolumeTotalBytes - result.VolumeFreeBytes : null,
            folders,
            [.. result.Extensions.Take(MaxExtensionsPerSnapshot).Select(e => new FolderStat(e.Extension.Length == 0 ? "(sem extensão)" : e.Extension, e.Bytes))]);

        lock (_gate)
        {
            List<Snapshot> all = [.. LoadUnlocked(), snapshot];
            if (all.Count > Math.Max(1, maxSnapshots))
            {
                all = [.. all.OrderBy(s => s.CreatedUtc).Skip(all.Count - Math.Max(1, maxSnapshots))];
            }

            Save(all);
        }

        return snapshot;
    }

    public void Clear()
    {
        lock (_gate)
        {
            files.TryDeleteOwnFile(files.Paths.HistoryFile);
        }
    }

    /// <summary>Compares a snapshot with the previous one of the SAME root. Null if there is none.</summary>
    public static SnapshotComparison? CompareWithPrevious(IReadOnlyList<Snapshot> all, Snapshot current)
    {
        Snapshot? previous = all
            .Where(s => s.Id != current.Id && s.CreatedUtc < current.CreatedUtc && SameRoot(s.Root, current.Root))
            .OrderByDescending(s => s.CreatedUtc)
            .FirstOrDefault();
        if (previous is null)
        {
            return null;
        }

        var before = previous.Folders.ToDictionary(f => f.Name, f => f.Bytes, StringComparer.OrdinalIgnoreCase);
        var after = current.Folders.ToDictionary(f => f.Name, f => f.Bytes, StringComparer.OrdinalIgnoreCase);
        var deltas = new List<FolderDelta>();
        foreach (string name in before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase))
        {
            bool hadBefore = before.TryGetValue(name, out long b);
            bool hasAfter = after.TryGetValue(name, out long a);
            DeltaKind kind = !hadBefore ? DeltaKind.New
                : !hasAfter ? DeltaKind.Removed
                : a > b ? DeltaKind.Grew
                : a < b ? DeltaKind.Shrunk
                : DeltaKind.Same;
            deltas.Add(new FolderDelta(name, hadBefore ? b : 0, hasAfter ? a : 0, kind));
        }

        deltas.Sort(static (x, y) => Math.Abs(y.Delta).CompareTo(Math.Abs(x.Delta)));
        return new SnapshotComparison(previous, current, deltas);
    }

    public static bool SameRoot(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private List<Snapshot> LoadUnlocked()
    {
        string path = files.Paths.HistoryFile;
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            if (new FileInfo(path).Length > MaxFileBytes)
            {
                files.QuarantineCorrupt(path);
                return [];
            }

            FileModel? model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path), Json);
            if (model is null || model.Schema != Schema)
            {
                files.QuarantineCorrupt(path);
                return [];
            }

            return [.. model.Snapshots
                .Where(IsValid)
                .OrderBy(s => s.CreatedUtc)
                .TakeLast(Math.Max(1, maxSnapshots))];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            try
            {
                files.QuarantineCorrupt(path);
            }
            catch (Exception inner) when (inner is IOException or UnauthorizedAccessException)
            {
                // Could not even move it aside; carry on with an empty history in memory.
            }

            return [];
        }
    }

    private static bool IsValid(Snapshot s) =>
        s is not null
        && !string.IsNullOrWhiteSpace(s.Root)
        && s.TotalBytes >= 0
        && s.FileCount >= 0
        && s.Folders is not null
        && s.Extensions is not null
        && s.Folders.All(f => f is not null && f.Name is not null && f.Bytes >= 0);

    private void Save(List<Snapshot> all)
    {
        files.WriteAllTextAtomic(files.Paths.HistoryFile, JsonSerializer.Serialize(new FileModel { Schema = Schema, Snapshots = all }, Json));
    }
}

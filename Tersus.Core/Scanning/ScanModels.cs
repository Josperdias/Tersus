namespace Tersus.Core.Scanning;

public sealed record ScanOptions
{
    /// <summary>How many of the largest files to keep (the UI shows them in a searchable list).</summary>
    public int LargestFilesCount { get; init; } = 300;

    /// <summary>
    /// Directory-listing threads. Listing is bound by I/O latency (a cold or network-backed disk answers one request at a time), so a few more
    /// workers than cores keep it busy; they run at below-normal priority so the PC stays responsive, and the cap of 8 avoids thrashing a spinning disk.
    /// </summary>
    public int MaxDegreeOfParallelism { get; init; } = Math.Clamp(Environment.ProcessorCount, 4, 8);

    /// <summary>Upper bound for folder nodes kept in memory; smaller folders are folded into their parent's "other".</summary>
    public int MaxRetainedNodes { get; init; } = 100_000;

    /// <summary>Folders smaller than this start out folded; the threshold grows automatically on huge trees.</summary>
    public long InitialNodeThresholdBytes { get; init; } = 1L << 20;

    public int ProgressIntervalMs { get; init; } = 150;

    public int MaxDepth { get; init; } = 400;

    /// <summary>How many distinct file extensions are tracked per worker before the rest is lumped together.</summary>
    public int MaxDistinctExtensions { get; init; } = 4000;
}

public readonly record struct ScanProgress(
    long Files,
    long Directories,
    long Bytes,
    long LinksSkipped,
    long InaccessibleDirectories,
    string? CurrentFolder,
    TimeSpan Elapsed);

public sealed record FileEntry(string Path, long Bytes, DateTime LastWriteUtc)
{
    public string Name => System.IO.Path.GetFileName(Path);

    public string Extension
    {
        get
        {
            string n = Name;
            int dot = n.LastIndexOf('.');
            return dot < 0 ? string.Empty : n[dot..].ToLowerInvariant();
        }
    }

    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
}

public sealed record ExtensionStat(string Extension, long Files, long Bytes);

/// <summary>
/// One folder in the size tree. Sizes are LOGICAL (sum of file lengths); links are never followed, so the
/// same data is not counted twice. Children are sorted by size, largest first; folders too small to list
/// individually are summarised by <see cref="OtherBytes"/> / <see cref="OtherFolders"/>.
/// </summary>
public sealed class SizeNode
{
    internal SizeNode(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public SizeNode? Parent { get; internal set; }

    /// <summary>Total logical bytes of this folder, including everything below it.</summary>
    public long Bytes { get; internal set; }

    /// <summary>Bytes of files that sit directly in this folder.</summary>
    public long DirectFilesBytes { get; internal set; }

    /// <summary>Files in this folder and below.</summary>
    public long FileCount { get; internal set; }

    /// <summary>Subfolders below this folder (all levels).</summary>
    public long FolderCount { get; internal set; }

    /// <summary>Bytes inside subfolders that are too small to list individually.</summary>
    public long OtherBytes { get; internal set; }

    public long OtherFolders { get; internal set; }

    public IReadOnlyList<SizeNode> Children { get; internal set; } = [];

    internal List<SizeNode>? KeptChildren { get; set; }

    public string FullPath
    {
        get
        {
            if (Parent is null)
            {
                return Name;
            }

            string parent = Parent.FullPath;
            return parent.EndsWith('\\') || parent.EndsWith('/') ? parent + Name : parent + System.IO.Path.DirectorySeparatorChar + Name;
        }
    }

    public IEnumerable<SizeNode> Descendants()
    {
        var stack = new Stack<SizeNode>();
        foreach (SizeNode c in Children)
        {
            stack.Push(c);
        }

        while (stack.Count > 0)
        {
            SizeNode n = stack.Pop();
            yield return n;
            foreach (SizeNode c in n.Children)
            {
                stack.Push(c);
            }
        }
    }
}

public sealed class ScanResult
{
    public required string Root { get; init; }

    public required SizeNode Tree { get; init; }

    public required DateTime StartedUtc { get; init; }

    public required TimeSpan Duration { get; init; }

    /// <summary>False when the scan was cancelled: totals, largest files and first-level folders are partial.</summary>
    public required bool Completed { get; init; }

    public long TotalBytes => Tree.Bytes;

    public long FileCount => Tree.FileCount;

    public long FolderCount => Tree.FolderCount;

    public required IReadOnlyList<FileEntry> LargestFiles { get; init; }

    public required IReadOnlyList<ExtensionStat> Extensions { get; init; }

    /// <summary>Symbolic links, junctions and mount points that were not followed.</summary>
    public long LinksSkipped { get; init; }

    /// <summary>Files that exist only in the cloud (OneDrive and similar); they do not use local disk space.</summary>
    public long CloudOnlyFiles { get; init; }

    public long CloudOnlyBytes { get; init; }

    public long InaccessibleFolders { get; init; }

    public IReadOnlyList<string> InaccessibleSamples { get; init; } = [];

    /// <summary>Size and free space of the volume that holds the root (null if unknown).</summary>
    public long? VolumeTotalBytes { get; init; }

    public long? VolumeFreeBytes { get; init; }

    public bool RootIsVolumeRoot { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Used space reported by the volume minus what was analysed (system files, other users, snapshots...). Only meaningful for a whole-drive scan.</summary>
    public long? UnaccountedBytes =>
        RootIsVolumeRoot && VolumeTotalBytes is not null && VolumeFreeBytes is not null
            ? Math.Max(0, VolumeTotalBytes.Value - VolumeFreeBytes.Value - TotalBytes)
            : null;
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;

namespace Tersus.Core.Scanning;

/// <summary>
/// Read-only disk usage analysis. Properties that matter for safety and for large drives:
/// <list type="bullet">
/// <item>never modifies anything, never follows symbolic links, junctions or mount points (no loops, no double counting);</item>
/// <item>memory is bounded: files are streamed (only counters, a top-N list and an extension table are kept) and folders are
/// kept in the result tree only when large enough, with an adaptive threshold and a hard node budget;</item>
/// <item>cancellable at any moment, returning partial first-level results instead of nothing;</item>
/// <item>sizes are LOGICAL sizes: hard links, compression, sparse files and cloud placeholders make physical use differ, and the
/// result says so (cloud-only files are counted separately and excluded from local totals).</item>
/// </list>
/// </summary>
public sealed class StorageScanner
{
    // FILE_ATTRIBUTE_RECALL_ON_OPEN / RECALL_ON_DATA_ACCESS: placeholder files whose data is not on the local disk.
    private const uint RecallOnOpen = 0x00040000;
    private const uint RecallOnDataAccess = 0x00400000;

    public ScanResult Scan(
        string root,
        ScanOptions? options = null,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return new Run(root, options ?? new ScanOptions(), progress, cancellationToken).Execute();
    }

    internal static bool IsCloudOnly(FileAttributes a) =>
        (a & FileAttributes.Offline) != 0 || ((uint)a & (RecallOnOpen | RecallOnDataAccess)) != 0;

    /// <summary>True for symlinks, junctions and mount points. When in doubt (error reading the link) it answers true: skipping is the safe side.</summary>
    internal static bool IsLink(string path, bool isDirectory)
    {
        try
        {
            FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
            return info.LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }

    private readonly record struct DirItem(string Name, FileAttributes Attributes);

    private sealed class ExtAcc
    {
        public long Files;
        public long Bytes;
    }

    private sealed class DirAccum
    {
        public long Files;
        public long Bytes;
        public long Cloud;
        public long CloudBytes;
        public long Links;
        public long FlushedFiles;
        public long FlushedBytes;

        public void Reset()
        {
            Files = Bytes = Cloud = CloudBytes = Links = FlushedFiles = FlushedBytes = 0;
        }
    }

    private sealed class WorkerState(int topN, int maxExtensions)
    {
        private readonly PriorityQueue<FileEntry, long> _heap = new();
        private readonly Dictionary<string, ExtAcc> _ext = new(StringComparer.OrdinalIgnoreCase);
        private long _heapMin = -1;

        public DirAccum Acc { get; } = new();

        public ExtAcc Other { get; } = new();

        public IReadOnlyCollection<(FileEntry Entry, long Priority)> HeapItems =>
            [.. _heap.UnorderedItems.Select(i => (i.Element, i.Priority))];

        public IReadOnlyDictionary<string, ExtAcc> Extensions => _ext;

        public bool WouldKeep(long length) => length > _heapMin;

        public void Offer(string path, long length, DateTime lastWriteUtc)
        {
            if (_heap.Count < topN)
            {
                _heap.Enqueue(new FileEntry(path, length, lastWriteUtc), length);
            }
            else if (length > _heap.Peek().Bytes)
            {
                _heap.Dequeue();
                _heap.Enqueue(new FileEntry(path, length, lastWriteUtc), length);
            }

            _heapMin = _heap.Count < topN ? -1 : _heap.Peek().Bytes;
        }

        public void AddExtension(ReadOnlySpan<char> fileName, long length)
        {
            int dot = fileName.LastIndexOf('.');
            ReadOnlySpan<char> ext = dot < 0 || dot == fileName.Length - 1 ? ReadOnlySpan<char>.Empty : fileName[dot..];
            var lookup = _ext.GetAlternateLookup<ReadOnlySpan<char>>();
            if (lookup.TryGetValue(ext, out ExtAcc? acc))
            {
                acc.Files++;
                acc.Bytes += length;
            }
            else if (_ext.Count < maxExtensions)
            {
                _ext[ext.ToString().ToLowerInvariant()] = new ExtAcc { Files = 1, Bytes = length };
            }
            else
            {
                Other.Files++;
                Other.Bytes += length;
            }
        }
    }

    private sealed class DirState(DirState? parent, string name, string path, int depth)
    {
        public readonly DirState? Parent = parent;
        public readonly string Name = name;
        public readonly int Depth = depth;
        public string? Path = path;

        // 1 for the folder's own listing + one per discovered subfolder that has not completed yet.
        public int Pending = 1;

        public long FilesBytes;
        public long FileCount;

        // Guarded by lock(this); written by completing children.
        public long ChildBytes;
        public long ChildFiles;
        public long ChildFolders;
        public long OtherBytes;
        public long OtherFolders;
        public List<SizeNode>? Kept;

        // First-level folders only: running total used for partial results after a cancellation.
        public long RunningBytes;
        public DirState? Top;
    }

    private sealed class Run
    {
        private readonly string _root;
        private readonly ScanOptions _opts;
        private readonly IProgress<ScanProgress>? _progress;
        private readonly CancellationToken _ct;
        private readonly BlockingCollection<DirState> _queue = new(new ConcurrentStack<DirState>());
        private readonly ConcurrentQueue<string> _inaccessibleSamples = new();
        private readonly ConcurrentQueue<string> _notes = new();
        private readonly ConcurrentBag<DirState> _topLevel = [];
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly object _growLock = new();

        private long _files;
        private long _dirs = 1;
        private long _bytes;
        private long _links;
        private long _inaccessible;
        private long _cloudFiles;
        private long _cloudBytes;
        private long _threshold;
        private int _retained;
        private int _nextGrowAt;
        private long _lastReportMs;
        private volatile string? _current;
        private SizeNode? _rootNode;
        private DirState? _rootState;

        public Run(string root, ScanOptions opts, IProgress<ScanProgress>? progress, CancellationToken ct)
        {
            _root = Path.GetFullPath(root);
            _opts = opts;
            _progress = progress;
            _ct = ct;
            _threshold = Math.Max(0, opts.InitialNodeThresholdBytes);
            _nextGrowAt = Math.Max(1000, opts.MaxRetainedNodes);
        }

        public ScanResult Execute()
        {
            if (!Directory.Exists(_root))
            {
                throw new DirectoryNotFoundException($"Pasta não encontrada: {_root}");
            }

            DateTime started = DateTime.UtcNow;
            _rootState = new DirState(null, _root, _root, 0);
            _queue.Add(_rootState);

            int threads = Math.Max(1, _opts.MaxDegreeOfParallelism);
            var workers = new List<WorkerState>();
            var running = new List<Thread>();
            for (int i = 0; i < threads; i++)
            {
                var state = new WorkerState(_opts.LargestFilesCount, _opts.MaxDistinctExtensions);
                workers.Add(state);
                var t = new Thread(() => WorkerLoop(state))
                {
                    IsBackground = true,
                    Priority = ThreadPriority.BelowNormal,
                    Name = "Tersus.Scan",
                };
                running.Add(t);
                t.Start();
            }

            foreach (Thread t in running)
            {
                t.Join();
            }

            bool completed = _rootNode is not null && !_ct.IsCancellationRequested;
            SizeNode tree = completed ? _rootNode! : BuildPartialTree();
            if (completed)
            {
                PruneToBudget(tree, _opts.MaxRetainedNodes);
            }

            _progress?.Report(Snapshot());
            return BuildResult(tree, completed, workers, started);
        }

        private ScanProgress Snapshot() =>
            new(
                Interlocked.Read(ref _files),
                Interlocked.Read(ref _dirs),
                Interlocked.Read(ref _bytes),
                Interlocked.Read(ref _links),
                Interlocked.Read(ref _inaccessible),
                _current,
                _clock.Elapsed);

        private void MaybeReport()
        {
            if (_progress is null)
            {
                return;
            }

            long now = _clock.ElapsedMilliseconds;
            long last = Interlocked.Read(ref _lastReportMs);
            if (now - last >= _opts.ProgressIntervalMs && Interlocked.CompareExchange(ref _lastReportMs, now, last) == last)
            {
                _progress.Report(Snapshot());
            }
        }

        private void WorkerLoop(WorkerState w)
        {
            try
            {
                foreach (DirState d in _queue.GetConsumingEnumerable(_ct))
                {
                    Process(d, w);
                }
            }
            catch (OperationCanceledException)
            {
                // Cancelled: the main thread builds a partial result.
            }
        }

        private void Process(DirState d, WorkerState w)
        {
            DirAccum acc = w.Acc;
            acc.Reset();
            string path = d.Path!;
            _current = path;

            var enumOptions = new EnumerationOptions
            {
                IgnoreInaccessible = false,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
                AttributesToSkip = 0,
            };

            try
            {
                var listing = new FileSystemEnumerable<DirItem>(
                    path,
                    (ref FileSystemEntry e) => new DirItem(e.FileName.ToString(), e.Attributes),
                    enumOptions)
                {
                    ShouldIncludePredicate = (ref FileSystemEntry e) =>
                    {
                        if (e.IsDirectory)
                        {
                            return true;
                        }

                        OnFile(ref e, w);
                        return false;
                    },
                };

                int counter = 0;
                foreach (DirItem item in listing)
                {
                    if ((++counter & 0xFF) == 0)
                    {
                        _ct.ThrowIfCancellationRequested();
                    }

                    string childPath = Path.Join(path, item.Name);
                    if ((item.Attributes & FileAttributes.ReparsePoint) != 0 && IsLink(childPath, isDirectory: true))
                    {
                        acc.Links++;
                        continue;
                    }

                    if (d.Depth + 1 > _opts.MaxDepth)
                    {
                        if (_notes.Count < 5)
                        {
                            _notes.Enqueue($"Pastas mais profundas que {_opts.MaxDepth} níveis não foram analisadas (ex.: {path}).");
                        }

                        continue;
                    }

                    var child = new DirState(d, item.Name, childPath, d.Depth + 1);
                    if (d.Depth == 0)
                    {
                        child.Top = child;
                        _topLevel.Add(child);
                    }
                    else
                    {
                        child.Top = d.Top;
                    }

                    // Count the child BEFORE it can possibly finish, so the parent cannot complete early.
                    Interlocked.Increment(ref d.Pending);
                    Interlocked.Increment(ref _dirs);
                    _queue.Add(child, _ct);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException or InvalidOperationException)
            {
                RecordInaccessible(path);
            }

            Flush(acc, d);
            d.Path = null;
            Interlocked.Add(ref _links, acc.Links);
            Interlocked.Add(ref _cloudFiles, acc.Cloud);
            Interlocked.Add(ref _cloudBytes, acc.CloudBytes);
            d.FilesBytes = acc.Bytes;
            d.FileCount = acc.Files;
            MaybeReport();
            Release(d);
        }

        private void OnFile(ref FileSystemEntry e, WorkerState w)
        {
            DirAccum acc = w.Acc;
            FileAttributes a = e.Attributes;
            long length = e.Length;

            if (IsCloudOnly(a))
            {
                acc.Cloud++;
                acc.CloudBytes += length;
                return;
            }

            if ((a & FileAttributes.ReparsePoint) != 0 && IsLink(e.ToFullPath(), isDirectory: false))
            {
                acc.Links++;
                return;
            }

            acc.Files++;
            acc.Bytes += length;
            w.AddExtension(e.FileName, length);
            if (length > 0 && w.WouldKeep(length))
            {
                w.Offer(e.ToFullPath(), length, e.LastWriteTimeUtc.UtcDateTime);
            }

            if ((acc.Files & 0xFFF) == 0)
            {
                // Keep the UI alive while a single enormous folder is being listed.
                Interlocked.Add(ref _files, acc.Files - acc.FlushedFiles);
                Interlocked.Add(ref _bytes, acc.Bytes - acc.FlushedBytes);
                acc.FlushedFiles = acc.Files;
                acc.FlushedBytes = acc.Bytes;
                MaybeReport();
            }
        }

        private void Flush(DirAccum acc, DirState d)
        {
            Interlocked.Add(ref _files, acc.Files - acc.FlushedFiles);
            Interlocked.Add(ref _bytes, acc.Bytes - acc.FlushedBytes);
            acc.FlushedFiles = acc.Files;
            acc.FlushedBytes = acc.Bytes;
            if (d.Top is not null)
            {
                Interlocked.Add(ref d.Top.RunningBytes, acc.Bytes);
            }
        }

        private void RecordInaccessible(string path)
        {
            Interlocked.Increment(ref _inaccessible);
            if (_inaccessibleSamples.Count < 25)
            {
                _inaccessibleSamples.Enqueue(path);
            }
        }

        /// <summary>Marks one unit of work on <paramref name="start"/> done and finishes every ancestor that became complete.</summary>
        private void Release(DirState start)
        {
            DirState? current = start;
            while (current is not null)
            {
                if (Interlocked.Decrement(ref current.Pending) != 0)
                {
                    return;
                }

                SizeNode node = Freeze(current);
                DirState? parent = current.Parent;
                if (parent is null)
                {
                    _rootNode = node;
                    _queue.CompleteAdding();
                    return;
                }

                Merge(parent, current, node);
                current = parent;
            }
        }

        private static SizeNode Freeze(DirState d)
        {
            var node = new SizeNode(d.Name)
            {
                DirectFilesBytes = d.FilesBytes,
                Bytes = d.FilesBytes + d.ChildBytes,
                FileCount = d.FileCount + d.ChildFiles,
                FolderCount = d.ChildFolders,
                OtherBytes = d.OtherBytes,
                OtherFolders = d.OtherFolders,
            };

            if (d.Kept is { Count: > 0 } kept)
            {
                kept.Sort(static (x, y) => y.Bytes.CompareTo(x.Bytes));
                foreach (SizeNode child in kept)
                {
                    child.Parent = node;
                }

                node.Children = kept;
            }

            return node;
        }

        private void Merge(DirState parent, DirState childState, SizeNode child)
        {
            lock (parent)
            {
                parent.ChildBytes += child.Bytes;
                parent.ChildFiles += child.FileCount;
                parent.ChildFolders += child.FolderCount + 1;

                // First-level folders are always listed; deeper ones only when big enough.
                bool keep = childState.Depth == 1 || child.Bytes >= Interlocked.Read(ref _threshold);
                if (keep)
                {
                    (parent.Kept ??= []).Add(child);
                    int retained = Interlocked.Increment(ref _retained);
                    if (retained > _nextGrowAt)
                    {
                        GrowThreshold(retained);
                    }
                }
                else
                {
                    parent.OtherBytes += child.Bytes;
                    parent.OtherFolders += child.FolderCount + 1;
                }
            }
        }

        private void GrowThreshold(int retained)
        {
            lock (_growLock)
            {
                if (retained > _nextGrowAt)
                {
                    long current = Interlocked.Read(ref _threshold);
                    Interlocked.Exchange(ref _threshold, Math.Max(current * 2, 1L << 20));
                    _nextGrowAt = retained + Math.Max(500, _opts.MaxRetainedNodes / 2);
                }
            }
        }

        /// <summary>Enforces the node budget exactly by folding the smallest folders into their parents.</summary>
        private static void PruneToBudget(SizeNode root, int budget)
        {
            var all = new List<SizeNode>();
            var stack = new Stack<SizeNode>(root.Children);
            while (stack.Count > 0)
            {
                SizeNode n = stack.Pop();
                all.Add(n);
                foreach (SizeNode c in n.Children)
                {
                    stack.Push(c);
                }
            }

            if (all.Count <= budget)
            {
                return;
            }

            long[] sizes = [.. all.Select(n => n.Bytes).OrderByDescending(b => b)];
            long cutoff = sizes[Math.Max(0, budget - 1)];

            var work = new Stack<SizeNode>();
            work.Push(root);
            while (work.Count > 0)
            {
                SizeNode n = work.Pop();
                if (n.Children.Count == 0)
                {
                    continue;
                }

                var keep = new List<SizeNode>(n.Children.Count);
                foreach (SizeNode c in n.Children)
                {
                    bool isFirstLevel = ReferenceEquals(n, root);
                    if (isFirstLevel || c.Bytes >= cutoff)
                    {
                        keep.Add(c);
                        work.Push(c);
                    }
                    else
                    {
                        n.OtherBytes += c.Bytes;
                        n.OtherFolders += c.FolderCount + 1;
                    }
                }

                n.Children = keep;
            }
        }

        private SizeNode BuildPartialTree()
        {
            var root = new SizeNode(_root)
            {
                FileCount = Interlocked.Read(ref _files),
                FolderCount = Math.Max(0, Interlocked.Read(ref _dirs) - 1),
            };

            var children = new List<SizeNode>();
            foreach (DirState t in _topLevel)
            {
                children.Add(new SizeNode(t.Name) { Bytes = Interlocked.Read(ref t.RunningBytes), Parent = root });
            }

            children.Sort(static (x, y) => y.Bytes.CompareTo(x.Bytes));
            long topBytes = children.Sum(c => c.Bytes);
            root.Children = children;
            root.Bytes = Math.Max(Interlocked.Read(ref _bytes), topBytes);
            root.DirectFilesBytes = Math.Max(0, root.Bytes - topBytes);
            return root;
        }

        private ScanResult BuildResult(SizeNode tree, bool completed, List<WorkerState> workers, DateTime started)
        {
            var largest = workers
                .SelectMany(w => w.HeapItems)
                .Select(i => i.Entry)
                .OrderByDescending(f => f.Bytes)
                .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .Take(_opts.LargestFilesCount)
                .ToList();

            var merged = new Dictionary<string, (long Files, long Bytes)>(StringComparer.OrdinalIgnoreCase);
            long otherFiles = 0;
            long otherBytes = 0;
            foreach (WorkerState w in workers)
            {
                foreach ((string ext, ExtAcc acc) in w.Extensions)
                {
                    merged.TryGetValue(ext, out (long Files, long Bytes) cur);
                    merged[ext] = (cur.Files + acc.Files, cur.Bytes + acc.Bytes);
                }

                otherFiles += w.Other.Files;
                otherBytes += w.Other.Bytes;
            }

            var extensions = merged
                .Select(kv => new ExtensionStat(kv.Key, kv.Value.Files, kv.Value.Bytes))
                .OrderByDescending(e => e.Bytes)
                .ToList();
            if (otherFiles > 0)
            {
                extensions.Add(new ExtensionStat("(outras)", otherFiles, otherBytes));
            }

            long? total = null;
            long? free = null;
            bool isVolumeRoot = false;
            try
            {
                string? pathRoot = Path.GetPathRoot(_root);
                if (!string.IsNullOrEmpty(pathRoot))
                {
                    var drive = new DriveInfo(pathRoot);
                    total = drive.TotalSize;
                    free = drive.TotalFreeSpace;
                    isVolumeRoot = string.Equals(_root.TrimEnd('\\', '/'), pathRoot.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
            {
                // Volume information is a nice-to-have.
            }

            return new ScanResult
            {
                Root = _root,
                Tree = tree,
                StartedUtc = started,
                Duration = _clock.Elapsed,
                Completed = completed,
                LargestFiles = largest,
                Extensions = extensions,
                LinksSkipped = Interlocked.Read(ref _links),
                CloudOnlyFiles = Interlocked.Read(ref _cloudFiles),
                CloudOnlyBytes = Interlocked.Read(ref _cloudBytes),
                InaccessibleFolders = Interlocked.Read(ref _inaccessible),
                InaccessibleSamples = [.. _inaccessibleSamples],
                VolumeTotalBytes = total,
                VolumeFreeBytes = free,
                RootIsVolumeRoot = isVolumeRoot,
                Notes = [.. _notes],
            };
        }
    }
}

using System.Buffers;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Security.Cryptography;
using Tersus.Core.IO;
using Tersus.Core.Knowledge;

namespace Tersus.Core.Duplicates;

/// <summary>
/// Finds files with identical content, for inspection only. Read-only: it never writes, moves or deletes anything.
/// Progressive comparison keeps it cheap and honest:
/// <list type="number">
/// <item>inventory (links not followed), keeping the largest files if there are too many;</item>
/// <item>same size is required to even be compared;</item>
/// <item>a quick fingerprint of the first and last bytes eliminates most look-alikes;</item>
/// <item>hard links to the same data are not duplicates and are ignored;</item>
/// <item>full SHA-256 only for what is left, within explicit file/byte budgets, largest potential first;
/// whatever could not be fully verified is reported as "not verified", never as a certain duplicate.</item>
/// </list>
/// </summary>
public sealed class DuplicateAnalyzer
{
    private readonly Func<string, FileIdentity?>? _identityOf;

    /// <param name="identityOf">Optional: returns the volume/file identity of a path so hard links to the same data can be recognised.</param>
    public DuplicateAnalyzer(Func<string, FileIdentity?>? identityOf = null)
    {
        _identityOf = identityOf;
    }

    private readonly record struct Item(string Path, long Size, DateTime Modified);

    public DuplicateResult Find(
        string root,
        DuplicateOptions? options = null,
        IProgress<DuplicateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        DuplicateOptions opt = options ?? new DuplicateOptions();
        string full = Path.GetFullPath(root);
        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"Pasta não encontrada: {full}");
        }

        var warnings = new List<string>();
        var clock = Stopwatch.StartNew();
        long lastReport = 0;

        void Report(string stage, long done, long total, string? current)
        {
            if (progress is null || clock.ElapsedMilliseconds - lastReport < opt.ProgressIntervalMs)
            {
                return;
            }

            lastReport = clock.ElapsedMilliseconds;
            progress.Report(new DuplicateProgress(stage, done, total, current));
        }

        // ---- 1. inventory -------------------------------------------------------------------------------------------
        long inventoryCount = 0;
        bool truncated = false;
        var heap = new PriorityQueue<Item, long>();
        var stack = new Stack<string>();
        stack.Push(full);
        bool cancelled = false;
        while (stack.Count > 0)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            string dir = stack.Pop();
            Report("Inventário", inventoryCount, 0, dir);
            try
            {
                var listing = new FileSystemEnumerable<(string Path, bool IsDir, FileAttributes Attr, long Length, DateTime Modified)>(
                    dir,
                    (ref FileSystemEntry e) => (e.ToFullPath(), e.IsDirectory, e.Attributes, e.IsDirectory ? 0 : e.Length, e.LastWriteTimeUtc.UtcDateTime),
                    new EnumerationOptions { IgnoreInaccessible = false, RecurseSubdirectories = false, AttributesToSkip = 0 })
                {
                    ShouldIncludePredicate = (ref FileSystemEntry e) => e.IsDirectory || e.Length >= Math.Max(1, opt.MinFileBytes),
                };

                foreach ((string path, bool isDir, FileAttributes attr, long length, DateTime modified) in listing)
                {
                    if ((attr & FileAttributes.ReparsePoint) != 0)
                    {
                        continue; // links and placeholders are never followed or compared
                    }

                    if (IsCloudPlaceholder(attr))
                    {
                        continue;
                    }

                    if (isDir)
                    {
                        stack.Push(path);
                        continue;
                    }

                    inventoryCount++;
                    if (heap.Count < opt.MaxInventoryFiles)
                    {
                        heap.Enqueue(new Item(path, length, modified), length);
                    }
                    else
                    {
                        truncated = true;
                        if (length > heap.Peek().Size)
                        {
                            heap.Dequeue();
                            heap.Enqueue(new Item(path, length, modified), length);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Unreadable folder: skipped, not fatal.
            }
        }

        if (truncated)
        {
            warnings.Add($"Havia mais de {opt.MaxInventoryFiles:N0} arquivos elegíveis; só os maiores foram comparados.");
        }

        // ---- 2. same size ---------------------------------------------------------------------------------------------
        var bySize = heap.UnorderedItems.Select(i => i.Element).GroupBy(i => i.Size).Where(g => g.Count() >= 2).ToList();
        long considered = bySize.Sum(g => (long)g.Count());

        // ---- 3. quick fingerprint (head + tail) -------------------------------------------------------------------------
        long unreadable = 0;
        long changed = 0;
        long sampled = 0;
        long bytesRead = 0;
        var sampleGroups = new List<List<Item>>();
        foreach (IGrouping<long, Item> sizeGroup in bySize)
        {
            if (cancelled || cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            var byFingerprint = new Dictionary<string, List<Item>>(StringComparer.Ordinal);
            foreach (Item item in sizeGroup)
            {
                Report("Comparando amostras", sampled, considered, item.Path);
                try
                {
                    string? fp = Fingerprint(item, opt.SampleBytes, cancellationToken, ref bytesRead);
                    sampled++;
                    if (fp is null)
                    {
                        changed++;
                        continue;
                    }

                    if (!byFingerprint.TryGetValue(fp, out List<Item>? list))
                    {
                        byFingerprint[fp] = list = [];
                    }

                    list.Add(item);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    break;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    unreadable++;
                }
            }

            foreach (List<Item> g in byFingerprint.Values.Where(g => g.Count >= 2))
            {
                sampleGroups.Add(g);
            }
        }

        // ---- 4. drop hard links to the same data -----------------------------------------------------------------------
        long hardLinks = 0;
        if (_identityOf is not null)
        {
            for (int i = 0; i < sampleGroups.Count; i++)
            {
                var seen = new HashSet<FileIdentity>();
                var kept = new List<Item>();
                foreach (Item item in sampleGroups[i])
                {
                    FileIdentity? id = _identityOf(item.Path);
                    if (id is not null && !seen.Add(id.Value))
                    {
                        hardLinks++;
                        continue;
                    }

                    kept.Add(item);
                }

                sampleGroups[i] = kept;
            }

            sampleGroups.RemoveAll(g => g.Count < 2);
        }

        // ---- 5. full hash within budgets, biggest potential first --------------------------------------------------------
        var groups = new List<DuplicateGroup>();
        long fullFiles = 0;
        long fullBytes = 0;
        bool budgetExhausted = false;
        foreach (List<Item> g in sampleGroups.OrderByDescending(g => g[0].Size * (g.Count - 1)))
        {
            if (cancelled || cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            long size = g[0].Size;
            bool affordable = fullFiles + g.Count <= opt.MaxFullyHashedFiles && fullBytes + (size * g.Count) <= opt.MaxFullyHashedBytes;
            if (!affordable)
            {
                budgetExhausted = true;
                groups.Add(new DuplicateGroup(size, null, [.. g.Select(ToFile)], Verified: false));
                continue;
            }

            var byHash = new Dictionary<string, List<Item>>(StringComparer.Ordinal);
            foreach (Item item in g)
            {
                Report("Verificando conteúdo", fullFiles, sampleGroups.Sum(x => (long)x.Count), item.Path);
                try
                {
                    string? hash = FullHash(item, cancellationToken, ref bytesRead);
                    fullFiles++;
                    fullBytes += size;
                    if (hash is null)
                    {
                        changed++;
                        continue;
                    }

                    if (!byHash.TryGetValue(hash, out List<Item>? list))
                    {
                        byHash[hash] = list = [];
                    }

                    list.Add(item);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    break;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    unreadable++;
                }
            }

            foreach ((string hash, List<Item> members) in byHash.Where(kv => kv.Value.Count >= 2))
            {
                groups.Add(new DuplicateGroup(size, hash, [.. members.Select(ToFile)], Verified: true));
            }
        }

        if (budgetExhausted)
        {
            warnings.Add("O limite de leitura foi atingido: alguns grupos têm só a mesma amostra e tamanho, mas o conteúdo completo NÃO foi verificado.");
        }

        if (unreadable > 0)
        {
            warnings.Add($"{unreadable:N0} arquivo(s) não puderam ser lidos (em uso ou sem permissão) e ficaram de fora.");
        }

        if (changed > 0)
        {
            warnings.Add($"{changed:N0} arquivo(s) mudaram durante a leitura e foram ignorados.");
        }

        var ordered = groups.OrderByDescending(g => g.Verified).ThenByDescending(g => g.PotentialBytes).ThenBy(g => g.Files[0].Path, StringComparer.OrdinalIgnoreCase).ToList();
        var stats = new DuplicateStats(inventoryCount, considered, sampled, fullFiles, bytesRead, unreadable, changed, hardLinks, truncated, budgetExhausted);
        progress?.Report(new DuplicateProgress("Concluído", fullFiles, fullFiles, null));
        return new DuplicateResult(ordered, stats, !cancelled, warnings);
    }

    private static DuplicateFile ToFile(Item i) => new(i.Path, i.Modified, ProtectedLocations.IsSensitive(i.Path));

    private static bool IsCloudPlaceholder(FileAttributes a) =>
        (a & FileAttributes.Offline) != 0 || ((uint)a & 0x00440000u) != 0;

    private static FileStream OpenForRead(string path) =>
        new(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.SequentialScan,
            BufferSize = 1,
        });

    /// <summary>SHA-256 of the first and last <paramref name="sample"/> bytes plus the size. Null if the file changed while being read.</summary>
    private static string? Fingerprint(Item item, int sample, CancellationToken ct, ref long bytesRead)
    {
        ct.ThrowIfCancellationRequested();
        using FileStream fs = OpenForRead(item.Path);
        if (fs.Length != item.Size)
        {
            return null;
        }

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(sample);
        try
        {
            int head = (int)Math.Min(sample, item.Size);
            ReadExactly(fs, buffer, head);
            sha.AppendData(buffer, 0, head);
            bytesRead += head;
            if (item.Size > head)
            {
                int tail = (int)Math.Min(sample, item.Size - head);
                fs.Seek(-tail, SeekOrigin.End);
                ReadExactly(fs, buffer, tail);
                sha.AppendData(buffer, 0, tail);
                bytesRead += tail;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return fs.Length == item.Size ? Convert.ToHexString(sha.GetHashAndReset()) : null;
    }

    private static string? FullHash(Item item, CancellationToken ct, ref long bytesRead)
    {
        using FileStream fs = OpenForRead(item.Path);
        if (fs.Length != item.Size)
        {
            return null;
        }

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            long total = 0;
            int read;
            while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                sha.AppendData(buffer, 0, read);
                total += read;
            }

            bytesRead += total;
            if (total != item.Size || new FileInfo(item.Path).Length != item.Size || File.GetLastWriteTimeUtc(item.Path) != item.Modified)
            {
                return null;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private static void ReadExactly(Stream s, byte[] buffer, int count)
    {
        int done = 0;
        while (done < count)
        {
            int n = s.Read(buffer, done, count - done);
            if (n <= 0)
            {
                throw new IOException("Unexpected end of file.");
            }

            done += n;
        }
    }
}

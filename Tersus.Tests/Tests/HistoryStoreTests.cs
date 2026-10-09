using System.Text.Json;
using Tersus.Core.History;
using Tersus.Core.Scanning;
using Tersus.Core.Storage;
using Tersus.Tests.Framework;
using Tersus.Tests.Support;

namespace Tersus.Tests;

[Category("history")]
public class HistoryStoreTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

    private static (TempDir Data, AppDataFiles Files) NewData()
    {
        var data = new TempDir("hist-data");
        var files = new AppDataFiles(new AppPaths(data.Combine("Tersus")));
        files.Paths.EnsureCreated();
        return (data, files);
    }

    private static ScanResult Scan(string root) => new StorageScanner().Scan(root);

    [Test]
    public void Records_and_loads_a_compact_summary()
    {
        (TempDir data, AppDataFiles files) = NewData();
        using (data)
        using (var tree = new TempDir("hist-tree"))
        {
            tree.WriteBytes("Documentos/secret-passwords.txt", new byte[1000]);
            tree.WriteBytes("Fotos/a.jpg", new byte[3000]);
            tree.WriteBytes("loose.bin", new byte[50]);
            var store = new HistoryStore(files);
            Snapshot? s = store.Record(Scan(tree.Path), T0);
            Assert.NotNull(s);
            IReadOnlyList<Snapshot> loaded = store.Load();
            Assert.Equal(1, loaded.Count);
            Assert.Equal(4050L, loaded[0].TotalBytes);
            Assert.Equal(3, loaded[0].FileCount);
            Assert.SequenceEqual(new[] { "Fotos", "Documentos", "(arquivos soltos na raiz)" }, loaded[0].Folders.Select(f => f.Name));
            string json = File.ReadAllText(files.Paths.HistoryFile);
            Assert.False(json.Contains("secret-passwords", StringComparison.Ordinal), "file names must never be stored");
            Assert.False(json.Contains("a.jpg", StringComparison.Ordinal));
            Assert.True(json.Contains("Documentos", StringComparison.Ordinal), "first-level folder names are stored");
        }
    }

    [Test]
    public void Keeps_only_the_newest_snapshots()
    {
        (TempDir data, AppDataFiles files) = NewData();
        using (data)
        using (var tree = new TempDir("hist-cap"))
        {
            tree.WriteBytes("a.bin", new byte[10]);
            var store = new HistoryStore(files, maxSnapshots: 3);
            ScanResult scan = Scan(tree.Path);
            for (int i = 0; i < 5; i++)
            {
                store.Record(scan, T0.AddDays(i));
            }

            IReadOnlyList<Snapshot> loaded = store.Load();
            Assert.Equal(3, loaded.Count);
            Assert.Equal(T0.AddDays(2), loaded[0].CreatedUtc);
            Assert.Equal(T0.AddDays(4), loaded[2].CreatedUtc);
        }

        Assert.Equal(24, HistoryStore.DefaultMaxSnapshots);
    }

    [Test]
    public void Cancelled_scans_are_not_stored()
    {
        (TempDir data, AppDataFiles files) = NewData();
        using (data)
        using (var tree = new TempDir("hist-cancel"))
        {
            tree.WriteBytes("a.bin", new byte[10]);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            ScanResult partial = new StorageScanner().Scan(tree.Path, null, null, cts.Token);
            var store = new HistoryStore(files);
            Assert.Null(store.Record(partial, T0));
            Assert.Equal(0, store.Load().Count);
        }
    }

    [Test]
    public void A_damaged_file_is_moved_aside_and_history_starts_again()
    {
        (TempDir data, AppDataFiles files) = NewData();
        using (data)
        using (var tree = new TempDir("hist-bad"))
        {
            tree.WriteBytes("a.bin", new byte[10]);
            File.WriteAllText(files.Paths.HistoryFile, "{ this is not json");
            var store = new HistoryStore(files);
            Assert.Equal(0, store.Load().Count);
            Assert.False(File.Exists(files.Paths.HistoryFile));
            Assert.True(Directory.EnumerateFiles(files.Paths.DataDirectory, "historico.json.corrompido-*").Any(), "the bad file is kept as evidence, not deleted");
            Assert.NotNull(store.Record(Scan(tree.Path), T0));
            Assert.Equal(1, store.Load().Count);
        }
    }

    [Test]
    public void Oversized_or_wrong_schema_files_are_quarantined()
    {
        (TempDir data, AppDataFiles files) = NewData();
        using (data)
        {
            File.WriteAllBytes(files.Paths.HistoryFile, new byte[5 * 1024 * 1024]);
            Assert.Equal(0, new HistoryStore(files).Load().Count);
            File.WriteAllText(files.Paths.HistoryFile, "{\"schema\": 99, \"snapshots\": []}");
            Assert.Equal(0, new HistoryStore(files).Load().Count);
            Assert.Equal(2, Directory.EnumerateFiles(files.Paths.DataDirectory, "historico.json.corrompido-*").Count());
        }
    }

    [Test]
    public void Invalid_entries_are_dropped_on_load()
    {
        (TempDir data, AppDataFiles files) = NewData();
        using (data)
        {
            string good = "{\"id\":\"" + Guid.NewGuid() + "\",\"createdUtc\":\"2026-01-01T00:00:00Z\",\"root\":\"C:\\\\\",\"totalBytes\":5,\"fileCount\":1,\"folders\":[],\"extensions\":[]}";
            string bad = "{\"id\":\"" + Guid.NewGuid() + "\",\"createdUtc\":\"2026-01-02T00:00:00Z\",\"root\":\"\",\"totalBytes\":-5,\"fileCount\":1,\"folders\":[],\"extensions\":[]}";
            File.WriteAllText(files.Paths.HistoryFile, "{\"schema\":1,\"snapshots\":[" + good + "," + bad + "]}");
            IReadOnlyList<Snapshot> loaded = new HistoryStore(files).Load();
            Assert.Equal(1, loaded.Count);
            Assert.Equal(5L, loaded[0].TotalBytes);
        }
    }

    [Test]
    public void Compares_with_the_previous_snapshot_of_the_same_root_only()
    {
        (TempDir data, AppDataFiles files) = NewData();
        using (data)
        using (var treeA = new TempDir("hist-a"))
        using (var treeB = new TempDir("hist-b"))
        {
            treeA.WriteBytes("Fotos/a.jpg", new byte[1000]);
            treeA.WriteBytes("Antiga/x.bin", new byte[400]);
            treeB.WriteBytes("Outra/b.bin", new byte[9000]);
            var store = new HistoryStore(files);
            Snapshot first = store.Record(Scan(treeA.Path), T0)!;
            store.Record(Scan(treeB.Path), T0.AddDays(1));
            treeA.WriteBytes("Fotos/b.jpg", new byte[2500]);
            treeA.WriteBytes("Nova/n.bin", new byte[700]);
            Directory.Delete(treeA.Combine("Antiga"), recursive: true);
            Snapshot current = store.Record(Scan(treeA.Path), T0.AddDays(7))!;

            SnapshotComparison? cmp = HistoryStore.CompareWithPrevious(store.Load(), current);
            Assert.NotNull(cmp);
            Assert.Equal(first.Id, cmp!.Before.Id);
            Assert.Equal(TimeSpan.FromDays(7), cmp.Elapsed);
            Assert.Equal((1000 + 2500 + 700) - (1000 + 400), cmp.TotalDelta);
            Assert.Equal("Fotos", cmp.Folders[0].Name);
            Assert.Equal(DeltaKind.Grew, cmp.Folders[0].Kind);
            Assert.Equal(2500L, cmp.Folders[0].Delta);
            Assert.Equal(DeltaKind.New, cmp.Folders.Single(f => f.Name == "Nova").Kind);
            Assert.Equal(DeltaKind.Removed, cmp.Folders.Single(f => f.Name == "Antiga").Kind);
            Assert.Null(HistoryStore.CompareWithPrevious(store.Load(), first), "the first snapshot of a root has nothing to compare with");
        }
    }

    [Test]
    public void Root_comparison_ignores_case_and_trailing_separators()
    {
        Assert.True(HistoryStore.SameRoot(@"C:\Users\", @"c:\users"));
        Assert.False(HistoryStore.SameRoot(@"C:\Users", @"C:\Users2"));
    }

    [Test]
    public void Clear_removes_the_history_file_only()
    {
        (TempDir data, AppDataFiles files) = NewData();
        using (data)
        using (var tree = new TempDir("hist-clear"))
        {
            tree.WriteBytes("a.bin", new byte[10]);
            File.WriteAllText(Path.Combine(files.Paths.DataDirectory, "unrelated.txt"), "keep");
            var store = new HistoryStore(files);
            store.Record(Scan(tree.Path), T0);
            store.Clear();
            Assert.False(File.Exists(files.Paths.HistoryFile));
            Assert.True(File.Exists(Path.Combine(files.Paths.DataDirectory, "unrelated.txt")));
            Assert.True(File.Exists(Path.Combine(tree.Path, "a.bin")), "the analysed folder is never touched");
        }
    }

    [Test]
    public void Saves_are_atomic_and_leave_no_temporary_files()
    {
        (TempDir data, AppDataFiles files) = NewData();
        using (data)
        using (var tree = new TempDir("hist-atomic"))
        {
            tree.WriteBytes("a.bin", new byte[10]);
            var store = new HistoryStore(files);
            for (int i = 0; i < 5; i++)
            {
                store.Record(Scan(tree.Path), T0.AddDays(i));
            }

            string[] names = Directory.EnumerateFiles(files.Paths.DataDirectory).Select(Path.GetFileName).OfType<string>().ToArray();
            Assert.SequenceEqual(new[] { "historico.json" }, names);
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(files.Paths.HistoryFile));
            Assert.Equal(1, doc.RootElement.GetProperty("schema").GetInt32());
        }
    }

    [Test]
    public void The_data_folder_guard_rejects_anything_outside_it()
    {
        (TempDir data, AppDataFiles files) = NewData();
        using (data)
        {
            string outside = Path.Combine(Path.GetTempPath(), "tersus-evil-" + Guid.NewGuid().ToString("N") + ".txt");
            Assert.Throws<InvalidOperationException>(() => files.WriteAllTextAtomic(outside, "x"));
            Assert.Throws<InvalidOperationException>(() => files.AppendLine(outside, "x"));
            Assert.Throws<InvalidOperationException>(() => files.QuarantineCorrupt(outside));
            Assert.Throws<InvalidOperationException>(() => files.WriteAllTextAtomic(Path.Combine(files.Paths.DataDirectory, "..", "evil.txt"), "x"));
            Assert.False(files.TryDeleteOwnFile(outside));
            Assert.False(File.Exists(outside));

            // A sibling folder that merely shares the prefix is outside, too.
            string sibling = files.Paths.DataDirectory + "-evil";
            Directory.CreateDirectory(sibling);
            Assert.Throws<InvalidOperationException>(() => files.WriteAllTextAtomic(Path.Combine(sibling, "x.txt"), "x"));
        }
    }

    [Test]
    public void Cleanup_logs_are_trimmed_to_the_newest()
    {
        (TempDir data, AppDataFiles files) = NewData();
        using (data)
        {
            for (int i = 1; i <= 6; i++)
            {
                File.WriteAllText(Path.Combine(files.Paths.LogsDirectory, $"limpeza-2026010{i}-120000.log"), "x");
            }

            File.WriteAllText(Path.Combine(files.Paths.LogsDirectory, "notes.txt"), "keep");
            Assert.Equal(4, files.TrimCleanupLogs(2));
            Assert.Equal(3, Directory.EnumerateFiles(files.Paths.LogsDirectory).Count());
            Assert.True(File.Exists(Path.Combine(files.Paths.LogsDirectory, "limpeza-20260106-120000.log")));
            Assert.True(File.Exists(Path.Combine(files.Paths.LogsDirectory, "notes.txt")));
        }
    }

    [Test]
    public void The_cleanup_log_appends_lines_inside_the_data_folder()
    {
        (TempDir data, AppDataFiles files) = NewData();
        using (data)
        {
            string path = files.Paths.NewCleanupLogPath(new DateTime(2026, 10, 9, 15, 30, 0));
            var log = new FileCleanupLog(files, path);
            log.Append("linha 1 \u00E7\u00E3o");
            log.Append("linha 2");
            string[] lines = File.ReadAllLines(path);
            Assert.SequenceEqual(new[] { "linha 1 \u00E7\u00E3o", "linha 2" }, lines);
            Assert.True(path.EndsWith("limpeza-20261009-153000.log", StringComparison.Ordinal));
        }
    }
}

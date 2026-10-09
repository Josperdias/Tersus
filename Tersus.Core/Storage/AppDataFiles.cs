using System.Text;

namespace Tersus.Core.Storage;

/// <summary>
/// The ONLY place in the product that is allowed to delete or replace files, and it can only touch Tersus' own data
/// folder (history and logs). Every call re-checks that the target really is inside that folder; a path anywhere else
/// is rejected. The static audit in the test suite enforces that no other source file calls a delete API.
/// </summary>
public sealed class AppDataFiles(AppPaths paths)
{
    public AppPaths Paths { get; } = paths;

    /// <summary>Writes through a temporary file and an atomic replace, so a crash never leaves a half-written file.</summary>
    public void WriteAllTextAtomic(string path, string content)
    {
        EnsureInsideDataDirectory(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDeleteOwnFile(temp);
            throw;
        }
    }

    /// <summary>Moves a corrupted file aside (never deletes the evidence).</summary>
    public string? QuarantineCorrupt(string path)
    {
        EnsureInsideDataDirectory(path);
        if (!File.Exists(path))
        {
            return null;
        }

        string target = path + ".corrompido-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        File.Move(path, target, overwrite: true);
        return target;
    }

    public bool TryDeleteOwnFile(string path)
    {
        try
        {
            EnsureInsideDataDirectory(path);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Keeps the newest <paramref name="keep"/> cleanup logs and removes older ones.</summary>
    public int TrimCleanupLogs(int keep)
    {
        if (!Directory.Exists(Paths.LogsDirectory))
        {
            return 0;
        }

        var old = new DirectoryInfo(Paths.LogsDirectory)
            .EnumerateFiles("limpeza-*.log")
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .Skip(Math.Max(0, keep))
            .ToList();
        int removed = 0;
        foreach (FileInfo f in old)
        {
            if (TryDeleteOwnFile(f.FullName))
            {
                removed++;
            }
        }

        return removed;
    }

    private void EnsureInsideDataDirectory(string path)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetFullPath(Paths.DataDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        StringComparison cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(root, cmp))
        {
            throw new InvalidOperationException($"Refusing to touch a file outside the Tersus data folder: {full}");
        }
    }
}

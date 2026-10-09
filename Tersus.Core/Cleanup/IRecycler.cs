namespace Tersus.Core.Cleanup;

public sealed record RecyclerPreflight(bool Ready, string Message, long? BinCapacityBytes = null);

public enum RecycleStatus
{
    /// <summary>The file left its folder AND a matching Recycle Bin entry was found.</summary>
    Recycled,

    /// <summary>Nothing happened to the file.</summary>
    NotMoved,

    /// <summary>The operation reported a problem; the file is still in place.</summary>
    Failed,

    /// <summary>The file is gone but no Recycle Bin entry could be found. Treated as a possible permanent deletion: the batch stops.</summary>
    GoneWithoutProof,
}

public sealed record RecycleOutcome(RecycleStatus Status, string Message);

/// <summary>
/// Sends single files to the Windows Recycle Bin. Implementations must NEVER delete permanently and must NEVER empty the bin.
/// </summary>
public interface IRecycler
{
    /// <summary>Checks, without touching any user file, that the Recycle Bin works for the volume that holds <paramref name="tempRoot"/>.</summary>
    RecyclerPreflight Preflight(string tempRoot);

    /// <summary>Moves exactly one file to the Recycle Bin and verifies it arrived there.</summary>
    RecycleOutcome Recycle(string path, long expectedLength);
}

public interface ICleanupLog
{
    string? Path { get; }

    void Append(string line);
}

public sealed class NullCleanupLog : ICleanupLog
{
    public static readonly NullCleanupLog Instance = new();

    private NullCleanupLog()
    {
    }

    public string? Path => null;

    public void Append(string line)
    {
    }
}

public sealed class FileCleanupLog : ICleanupLog
{
    private readonly object _gate = new();

    public FileCleanupLog(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
    }

    public string? Path { get; }

    public void Append(string line)
    {
        lock (_gate)
        {
            File.AppendAllText(Path!, line + Environment.NewLine, new System.Text.UTF8Encoding(false));
        }
    }
}

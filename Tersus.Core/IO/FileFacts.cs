namespace Tersus.Core.IO;

/// <summary>
/// Stable identity of a file on a volume (volume serial + file ID). Two paths with the same
/// identity are the same file (hard links); a changed identity means the file was replaced.
/// </summary>
public readonly record struct FileIdentity(ulong VolumeSerial, ulong IdLow, ulong IdHigh)
{
    public override string ToString() => $"{VolumeSerial:X}:{IdHigh:X}{IdLow:X16}";
}

public enum ProbeError
{
    None = 0,
    NotFound,
    AccessDenied,
    SharingViolation,
    PathTooLong,
    Other,
}

public readonly record struct VolumeSpace(long TotalBytes, long FreeBytes);

/// <summary>One item of a directory listing (cheap data only: no handle was opened for it).</summary>
public readonly record struct DirectoryEntry(
    string Name,
    bool IsDirectory,
    FileAttributes Attributes,
    long Length,
    DateTime CreatedUtc,
    DateTime ModifiedUtc);

/// <summary>
/// Everything the policy needs to know about one path, captured at one moment.
/// "Deep" facts come from an open handle (identity, link count, final path); they are what the
/// policy trusts before anything is moved. Facts taken from a directory listing are "shallow".
/// </summary>
public sealed record FileFacts
{
    public required string Path { get; init; }

    public bool Exists { get; init; }

    public bool IsDirectory { get; init; }

    public FileAttributes Attributes { get; init; }

    public long Length { get; init; }

    public DateTime CreationTimeUtc { get; init; }

    public DateTime LastWriteTimeUtc { get; init; }

    /// <summary>Number of hard links to the file (null = unknown).</summary>
    public uint? HardLinkCount { get; init; }

    public FileIdentity? Identity { get; init; }

    /// <summary>Path of the object really reached, with every link and 8.3 alias resolved (null = unknown).</summary>
    public string? FinalPath { get; init; }

    /// <summary>True when identity, link count and final path were read through an open handle.</summary>
    public bool IsDeep { get; init; }

    public ProbeError Error { get; init; }

    public string? ErrorDetail { get; init; }

    public bool IsReparsePoint => (Attributes & FileAttributes.ReparsePoint) != 0;

    public static FileFacts Missing(string path, ProbeError error = ProbeError.NotFound, string? detail = null) =>
        new() { Path = path, Exists = false, Error = error, ErrorDetail = detail };
}

/// <summary>
/// File-system access needed by the safety checks. Production uses the Windows implementation;
/// tests use an in-memory fake so every refusal path can be exercised on any OS.
/// </summary>
public interface IFileSystem
{
    /// <summary>Reads metadata WITHOUT following a link in the final component. Never throws for I/O problems.</summary>
    FileFacts Probe(string path);

    /// <summary>
    /// Opens the file so that nobody can write to it or swap it while the returned handle lives, and
    /// reports the facts observed through that handle. On failure <paramref name="hold"/> is null and
    /// the facts carry the error. Dispose the hold to release the file.
    /// </summary>
    FileFacts TryHold(string path, out IDisposable? hold);

    VolumeSpace? GetVolumeSpace(string path);

    /// <summary>
    /// Lists the direct children of a directory (links are listed, never followed). Throws
    /// <see cref="UnauthorizedAccessException"/> or <see cref="IOException"/> when the directory cannot be read.
    /// </summary>
    IEnumerable<DirectoryEntry> EnumerateDirectory(string path);
}

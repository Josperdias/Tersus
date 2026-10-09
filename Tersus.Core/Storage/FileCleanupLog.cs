using Tersus.Core.Cleanup;

namespace Tersus.Core.Storage;

/// <summary>Cleanup audit log: one line per file, flushed as it happens so a crash still leaves a trail. Lives in %LOCALAPPDATA%\Tersus\logs.</summary>
public sealed class FileCleanupLog : ICleanupLog
{
    private readonly object _gate = new();
    private readonly AppDataFiles _files;

    public FileCleanupLog(AppDataFiles files, string path)
    {
        _files = files;
        Path = path;
    }

    public string? Path { get; }

    public void Append(string line)
    {
        lock (_gate)
        {
            _files.AppendLine(Path!, line);
        }
    }
}

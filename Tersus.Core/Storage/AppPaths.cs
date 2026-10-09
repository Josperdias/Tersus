namespace Tersus.Core.Storage;

/// <summary>
/// Where Tersus keeps its own small files: %LOCALAPPDATA%\Tersus (history and cleanup logs).
/// Nothing is ever written anywhere else, and nothing is sent anywhere.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string? dataDirectory = null)
    {
        DataDirectory = dataDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tersus");
    }

    public string DataDirectory { get; }

    public string HistoryFile => Path.Combine(DataDirectory, "historico.json");

    public string LogsDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>Where the one-per-session Recycle Bin self-test file is created (see ShellRecycler).</summary>
    public string CanaryDirectory => Path.Combine(DataDirectory, "teste-lixeira");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }

    public string NewCleanupLogPath(DateTime localNow) =>
        Path.Combine(LogsDirectory, $"limpeza-{localNow:yyyyMMdd-HHmmss}.log");
}

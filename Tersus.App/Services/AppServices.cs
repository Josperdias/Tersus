using Tersus.Core;
using Tersus.Core.Apps;
using Tersus.Core.History;
using Tersus.Core.Scanning;
using Tersus.Core.Storage;
using Tersus.Core.Windows;

namespace Tersus.App.Services;

/// <summary>Everything the view models need, created once at startup (real services) or by the smoke test (same real services, different data folder).</summary>
public sealed class AppServices
{
    public required AppPaths Paths { get; init; }

    public required AppDataFiles DataFiles { get; init; }

    public required HistoryStore History { get; init; }

    public required CleanupServices.Stack Cleanup { get; init; }

    public required IAppCatalog Apps { get; init; }

    public required IDialogService Dialogs { get; set; }

    public StorageScanner Scanner { get; } = new();

    public IClock Clock { get; } = SystemClock.Instance;

    /// <summary>Restricts the temporary-file search to one folder INSIDE TEMP (used only by the smoke test so screenshots never show real file names). Null = the whole TEMP folder.</summary>
    public string? CleanupStartFolder { get; set; }

    public static AppServices Create(IDialogService dialogs, AppPaths? paths = null)
    {
        paths ??= new AppPaths();
        paths.EnsureCreated();
        var files = new AppDataFiles(paths);
        files.TrimCleanupLogs(30);
        return new AppServices
        {
            Paths = paths,
            DataFiles = files,
            History = new HistoryStore(files),
            Cleanup = CleanupServices.Create(paths),
            Apps = new RegistryAppCatalog(),
            Dialogs = dialogs,
        };
    }
}

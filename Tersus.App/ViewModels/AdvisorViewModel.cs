using System.Collections.ObjectModel;
using System.Windows.Input;
using Tersus.App.Services;
using Tersus.Core.Advice;
using Tersus.Core.Apps;
using Tersus.Core.Scanning;
using Tersus.Core.Windows;

namespace Tersus.App.ViewModels;

public sealed class AdviceRow(Recommendation recommendation)
{
    public Recommendation Recommendation { get; } = recommendation;

    public string Title => Recommendation.Title;

    public string Detail => Recommendation.Detail;

    public string? Evidence => Recommendation.Evidence;

    public string? HowTo => Recommendation.HowTo;

    public string? Caution => Recommendation.Caution;

    public AdviceSeverity Severity => Recommendation.Severity;

    public string SeverityText => Recommendation.Severity switch
    {
        AdviceSeverity.Important => "IMPORTANTE",
        AdviceSeverity.Attention => "ATENÇÃO",
        _ => "INFORMAÇÃO",
    };
}

/// <summary>Read-only guidance (hibernation file, Windows.old, Recycle Bin, Downloads...). It explains and points to the Windows way to do it; it changes nothing.</summary>
public sealed class AdvisorViewModel : ObservableObject, IPageActivated
{
    private readonly MainViewModel _main;
    private bool _busy;
    private string _status = string.Empty;

    public AdvisorViewModel(MainViewModel main)
    {
        _main = main;
        RefreshCommand = new AsyncCommand(LoadAsync, () => !_busy, ex => main.ReportError("Não foi possível montar as recomendações", ex));
        OpenRecycleBinCommand = new RelayCommand(ShellLauncher.OpenRecycleBin);
        OpenSettingsCommand = new RelayCommand(ShellLauncher.OpenAppsSettings);
        main.ScanChanged += (_, _) => _ = RefreshQuietlyAsync();
    }

    public ObservableCollection<AdviceRow> Items { get; } = [];

    public bool IsBusy { get => _busy; private set => Set(ref _busy, value); }

    public string Status { get => _status; private set => Set(ref _status, value); }

    public bool HasItems => Items.Count > 0;

    public ICommand RefreshCommand { get; }

    public ICommand OpenRecycleBinCommand { get; }

    public ICommand OpenSettingsCommand { get; }

    public void OnActivated()
    {
        if (Items.Count == 0 && !_busy)
        {
            _ = RefreshQuietlyAsync();
        }
    }

    public async Task LoadAsync()
    {
        IsBusy = true;
        Status = "Verificando (somente leitura)…";
        try
        {
            ScanResult? scan = _main.CurrentScan;
            IReadOnlyList<InstalledApp> known = _main.Apps.Loaded;
            IAppCatalog catalog = _main.Services.Apps;
            string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            IReadOnlyList<Recommendation> advice = await Task.Run(() =>
            {
                IReadOnlyList<InstalledApp> apps = known.Count > 0 ? known : catalog.List();
                long? downloadsBytes = scan is null ? null : FolderBytes(scan.Tree, downloads);
                AdvisorInput input = WindowsFacts.Collect(apps, scan?.CloudOnlyBytes, downloadsBytes);
                return SystemAdvisor.Build(input);
            });

            Items.Clear();
            foreach (Recommendation r in advice)
            {
                Items.Add(new AdviceRow(r));
            }

            OnPropertyChanged(nameof(HasItems));
            Status = Items.Count == 0
                ? "Nada especial para recomendar agora."
                : "O Tersus só explica: nenhuma destas configurações é alterada por ele. Siga os passos do próprio Windows, se quiser.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshQuietlyAsync()
    {
        try
        {
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Status = "Não foi possível montar as recomendações: " + ex.Message;
        }
    }

    private static long? FolderBytes(SizeNode tree, string path)
    {
        string wanted = path.TrimEnd('\\');
        foreach (SizeNode node in tree.Descendants())
        {
            if (string.Equals(node.FullPath.TrimEnd('\\'), wanted, StringComparison.OrdinalIgnoreCase))
            {
                return node.Bytes;
            }
        }

        return null;
    }
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Input;
using Tersus.App.Services;
using Tersus.Core.Apps;
using ByteSize = Tersus.Core.SizeText;

namespace Tersus.App.ViewModels;

public sealed class AppRow(InstalledApp app)
{
    public InstalledApp App { get; } = app;

    public string Name => App.Name;

    public string Publisher => string.IsNullOrWhiteSpace(App.Publisher) ? "—" : App.Publisher;

    public string Version => string.IsNullOrWhiteSpace(App.Version) ? "—" : App.Version;

    public DateTime? InstallDate => App.InstallDate;

    public string InstallDateText => App.InstallDate is { } d ? d.ToString("dd/MM/yyyy", CultureInfo.CurrentCulture) : "—";

    public long SortBytes => App.EstimatedSizeBytes ?? -1;

    public string SizeText => App.EstimatedSizeBytes is { } b && b > 0 ? ByteSize.Format(b) : "não informado";

    public string Location => string.IsNullOrWhiteSpace(App.InstallLocation) ? "—" : App.InstallLocation;

    public string Source => App.Source;
}

/// <summary>Read-only list of installed programs. Tersus never uninstalls anything and never guesses "last used".</summary>
public sealed class AppsViewModel : ObservableObject, IPageActivated
{
    private readonly MainViewModel _main;
    private readonly ObservableCollection<AppRow> _rows = [];
    private bool _loaded;
    private bool _busy;
    private string _search = string.Empty;
    private int _sortIndex = 1;
    private AppRow? _selected;
    private string _summary = string.Empty;

    public AppsViewModel(MainViewModel main)
    {
        _main = main;
        View = CollectionViewSource.GetDefaultView(_rows);
        View.Filter = Matches;
        ApplySort();
        RefreshCommand = new AsyncCommand(LoadAsync, () => !_busy, ex => main.ReportError("Não foi possível ler a lista de programas", ex));
        OpenSettingsCommand = new RelayCommand(ShellLauncher.OpenAppsSettings);
        ExplorerCommand = new RelayCommand(() => ShellLauncher.OpenFolder(_selected?.App.InstallLocation), () => _selected is not null);
    }

    public ICollectionView View { get; }

    public IReadOnlyList<string> SortLabels { get; } = ["Nome", "Maiores primeiro (tamanho informado)", "Instalados há menos tempo"];

    public IReadOnlyList<InstalledApp> Loaded => [.. _rows.Select(r => r.App)];

    public bool IsBusy { get => _busy; private set => Set(ref _busy, value); }

    public string SearchText
    {
        get => _search;
        set
        {
            if (Set(ref _search, value))
            {
                View.Refresh();
                UpdateSummary();
            }
        }
    }

    public int SortIndex
    {
        get => _sortIndex;
        set
        {
            if (Set(ref _sortIndex, value))
            {
                ApplySort();
            }
        }
    }

    public AppRow? Selected { get => _selected; set => Set(ref _selected, value); }

    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    public ICommand RefreshCommand { get; }

    public ICommand OpenSettingsCommand { get; }

    public ICommand ExplorerCommand { get; }

    public void OnActivated()
    {
        if (!_loaded && !_busy)
        {
            _ = LoadSafelyAsync();
        }
    }

    public async Task LoadAsync()
    {
        IsBusy = true;
        Summary = "Lendo a lista de programas instalados (somente leitura)…";
        try
        {
            IReadOnlyList<InstalledApp> apps = await Task.Run(_main.Services.Apps.List);
            _rows.Clear();
            foreach (InstalledApp a in apps)
            {
                _rows.Add(new AppRow(a));
            }

            _loaded = true;
            UpdateSummary();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadSafelyAsync()
    {
        try
        {
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _main.ReportError("Não foi possível ler a lista de programas", ex);
        }
    }

    private bool Matches(object o) =>
        o is AppRow r
        && (string.IsNullOrWhiteSpace(_search)
            || r.Name.Contains(_search.Trim(), StringComparison.OrdinalIgnoreCase)
            || r.Publisher.Contains(_search.Trim(), StringComparison.OrdinalIgnoreCase));

    private void ApplySort()
    {
        View.SortDescriptions.Clear();
        switch (_sortIndex)
        {
            case 1:
                View.SortDescriptions.Add(new SortDescription(nameof(AppRow.SortBytes), ListSortDirection.Descending));
                View.SortDescriptions.Add(new SortDescription(nameof(AppRow.Name), ListSortDirection.Ascending));
                break;
            case 2:
                View.SortDescriptions.Add(new SortDescription(nameof(AppRow.InstallDate), ListSortDirection.Descending));
                break;
            default:
                View.SortDescriptions.Add(new SortDescription(nameof(AppRow.Name), ListSortDirection.Ascending));
                break;
        }
    }

    private void UpdateSummary()
    {
        int shown = View.Cast<object>().Count();
        Summary = string.Create(CultureInfo.CurrentCulture,
            $"{shown:N0} de {_rows.Count:N0} programas. Os tamanhos são os que cada instalador INFORMOU ao Windows (podem faltar ou estar errados). O Tersus não desinstala nada nem estima “último uso”: para remover um programa, use Aplicativos instalados do Windows.");
    }
}

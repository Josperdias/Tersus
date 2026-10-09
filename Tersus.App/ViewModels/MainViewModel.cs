using System.Globalization;
using System.Reflection;
using System.Windows.Input;
using Tersus.App.Services;
using Tersus.Core;
using Tersus.Core.Scanning;

namespace Tersus.App.ViewModels;

public interface IPageActivated
{
    void OnActivated();
}

public sealed record NavItem(string Id, string Title, string Hint, string IconKey, ObservableObject ViewModel);

public sealed class MainViewModel : ObservableObject
{
    private NavItem? _selected;
    private ScanResult? _scan;
    private double _uiScale = 1.0;
    private string _statusLine = "Nenhuma análise feita ainda. Escolha um local em “Início”.";

    public MainViewModel(AppServices services)
    {
        Services = services;
        Home = new HomeViewModel(this);
        Overview = new OverviewViewModel(this);
        LargeFiles = new LargeFilesViewModel(this);
        Duplicates = new DuplicatesViewModel(this);
        Cleanup = new CleanupViewModel(this);
        Apps = new AppsViewModel(this);
        History = new HistoryViewModel(this);
        Advisor = new AdvisorViewModel(this);
        About = new AboutViewModel(this);

        Pages =
        [
            new NavItem("home", "Início", "Escolha o que analisar", "IconHome", Home),
            new NavItem("overview", "Visão geral", "De onde vem o espaço", "IconOverview", Overview),
            new NavItem("files", "Maiores arquivos", "Os maiores, explicados", "IconFiles", LargeFiles),
            new NavItem("duplicates", "Duplicados", "Cópias idênticas (só relatório)", "IconDuplicates", Duplicates),
            new NavItem("cleanup", "Limpeza", "Temporários antigos → Lixeira", "IconCleanup", Cleanup),
            new NavItem("apps", "Aplicativos", "Programas instalados", "IconApps", Apps),
            new NavItem("history", "Histórico", "Como o disco cresceu", "IconHistory", History),
            new NavItem("advice", "Recomendações", "Orientações, sem alterar nada", "IconAdvice", Advisor),
            new NavItem("about", "Segurança e sobre", "O que o Tersus nunca faz", "IconShield", About),
        ];
        _selected = Pages[0];
        NavigateCommand = new RelayCommand(p => { if (p is string id) { Navigate(id); } });
        ZoomInCommand = new RelayCommand(() => UiScale = Math.Min(1.6, Math.Round(UiScale + 0.1, 1)));
        ZoomOutCommand = new RelayCommand(() => UiScale = Math.Max(0.8, Math.Round(UiScale - 0.1, 1)));
        ZoomResetCommand = new RelayCommand(() => UiScale = 1.0);
    }

    public AppServices Services { get; }

    public HomeViewModel Home { get; }

    public OverviewViewModel Overview { get; }

    public LargeFilesViewModel LargeFiles { get; }

    public DuplicatesViewModel Duplicates { get; }

    public CleanupViewModel Cleanup { get; }

    public AppsViewModel Apps { get; }

    public HistoryViewModel History { get; }

    public AdvisorViewModel Advisor { get; }

    public AboutViewModel About { get; }

    public IReadOnlyList<NavItem> Pages { get; }

    public ICommand NavigateCommand { get; }

    public ICommand ZoomInCommand { get; }

    public ICommand ZoomOutCommand { get; }

    public ICommand ZoomResetCommand { get; }

    public event EventHandler? ScanChanged;

    public NavItem? SelectedPage
    {
        get => _selected;
        set
        {
            if (value is not null && Set(ref _selected, value))
            {
                OnPropertyChanged(nameof(CurrentViewModel));
                (value.ViewModel as IPageActivated)?.OnActivated();
            }
        }
    }

    public object? CurrentViewModel => _selected?.ViewModel;

    public ScanResult? CurrentScan => _scan;

    public string StatusLine
    {
        get => _statusLine;
        private set => Set(ref _statusLine, value);
    }

    public double UiScale
    {
        get => _uiScale;
        set => Set(ref _uiScale, value);
    }

    public string VersionText
    {
        get
        {
            string? v = typeof(MainViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (v is null)
            {
                return "0.0.0";
            }

            int plus = v.IndexOf('+', StringComparison.Ordinal);
            return plus > 0 ? v[..plus] : v;
        }
    }

    public void Navigate(string id)
    {
        NavItem? page = Pages.FirstOrDefault(p => p.Id == id);
        if (page is not null)
        {
            SelectedPage = page;
        }
    }

    /// <summary>Called when an analysis finishes (completed or cancelled). Only completed analyses are written to the history.</summary>
    public void PublishScan(ScanResult scan)
    {
        _scan = scan;
        if (scan.Completed)
        {
            try
            {
                Services.History.Record(scan, DateTime.UtcNow);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // The history is a convenience; never let it break an analysis.
            }
        }

        CultureInfo c = CultureInfo.CurrentCulture;
        StatusLine = string.Create(c, $"{(scan.Completed ? "Analisado" : "Análise parcial")}: {scan.Root}  •  {SizeText.Format(scan.TotalBytes, c)} em {scan.FileCount:N0} arquivos  •  {scan.Duration:mm\\:ss}");
        ScanChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows a short status line in the window footer.</summary>
    public void NotifyStatus(string text) => StatusLine = text;

    public void ReportError(string title, Exception ex, bool filesMayHaveChanged = false) =>
        Services.Dialogs.ShowMessage(
            title,
            (filesMayHaveChanged
                ? "Algo inesperado aconteceu durante a limpeza. Alguns arquivos podem já ter sido enviados à Lixeira do Windows: abra a Lixeira e o registro da limpeza para conferir antes de tentar de novo."
                : "Algo inesperado aconteceu, mas nada foi alterado nos seus arquivos.")
            + "\n\nDetalhe técnico: " + ex.Message);
}

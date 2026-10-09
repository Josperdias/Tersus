using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Tersus.Core;
using Tersus.Core.Scanning;

namespace Tersus.App.ViewModels;

public sealed class DriveRow(DriveEntry entry)
{
    public DriveEntry Entry { get; } = entry;

    public string Name => Entry.DisplayName;

    public string Detail =>
        string.Create(CultureInfo.CurrentCulture, $"{SizeText.Format(Entry.FreeBytes)} livres de {SizeText.Format(Entry.TotalBytes)}  •  {Entry.Format}");

    public double UsedFraction => Entry.UsedFraction;

    public string UsedText => Entry.UsedFraction.ToString("P0", CultureInfo.CurrentCulture) + " usado";
}

public sealed class HomeViewModel : ObservableObject, IPageActivated
{
    private readonly MainViewModel _main;
    private DriveRow? _selectedDrive;
    private string? _customFolder;
    private bool _busy;
    private string _status = "Escolha uma unidade ou uma pasta e clique em “Analisar”. Nada é alterado, apagado ou enviado a lugar nenhum.";
    private string _filesText = "0";
    private string _bytesText = "0 B";
    private string _foldersText = "0";
    private string _currentFolder = string.Empty;
    private string _elapsed = "00:00";
    private string? _summary;
    private CancellationTokenSource? _cts;

    public HomeViewModel(MainViewModel main)
    {
        _main = main;
        StartCommand = new AsyncCommand(StartAsync, () => HasTarget && !IsBusy, ex => main.ReportError("Não foi possível analisar", ex));
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        ChooseFolderCommand = new RelayCommand(ChooseFolder, () => !IsBusy);
        RefreshDrivesCommand = new RelayCommand(RefreshDrives, () => !IsBusy);
        GoOverviewCommand = new RelayCommand(() => main.Navigate("overview"));
        GoFilesCommand = new RelayCommand(() => main.Navigate("files"));
        GoDuplicatesCommand = new RelayCommand(() => main.Navigate("duplicates"));
        GoCleanupCommand = new RelayCommand(() => main.Navigate("cleanup"));
        RefreshDrives();
    }

    public ObservableCollection<DriveRow> Drives { get; } = [];

    public DriveRow? SelectedDrive
    {
        get => _selectedDrive;
        set
        {
            if (Set(ref _selectedDrive, value) && value is not null)
            {
                _customFolder = null;
                OnPropertyChanged(nameof(CustomFolder));
            }

            OnPropertyChanged(nameof(HasTarget));
            OnPropertyChanged(nameof(TargetText));
        }
    }

    public string? CustomFolder
    {
        get => _customFolder;
        private set
        {
            if (Set(ref _customFolder, value))
            {
                if (value is not null)
                {
                    _selectedDrive = null;
                    OnPropertyChanged(nameof(SelectedDrive));
                }

                OnPropertyChanged(nameof(HasTarget));
                OnPropertyChanged(nameof(TargetText));
            }
        }
    }

    public bool HasTarget => Target is not null;

    public string? Target => _customFolder ?? _selectedDrive?.Entry.Root;

    public string TargetText => Target is null ? "Nenhum local escolhido" : $"Local a analisar: {Target}";

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (Set(ref _busy, value))
            {
                OnPropertyChanged(nameof(IsIdle));
            }
        }
    }

    public bool IsIdle => !_busy;

    public string Status { get => _status; private set => Set(ref _status, value); }

    public string FilesText { get => _filesText; private set => Set(ref _filesText, value); }

    public string BytesText { get => _bytesText; private set => Set(ref _bytesText, value); }

    public string FoldersText { get => _foldersText; private set => Set(ref _foldersText, value); }

    public string CurrentFolder { get => _currentFolder; private set => Set(ref _currentFolder, value); }

    public string Elapsed { get => _elapsed; private set => Set(ref _elapsed, value); }

    public string? Summary { get => _summary; private set { Set(ref _summary, value); OnPropertyChanged(nameof(HasSummary)); } }

    public bool HasSummary => _summary is not null;

    public ICommand StartCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand ChooseFolderCommand { get; }

    public ICommand RefreshDrivesCommand { get; }

    public ICommand GoOverviewCommand { get; }

    public ICommand GoFilesCommand { get; }

    public ICommand GoDuplicatesCommand { get; }

    public ICommand GoCleanupCommand { get; }

    public void OnActivated()
    {
    }

    /// <summary>Lets tests/smoke choose the folder directly.</summary>
    public void SetFolder(string folder) => CustomFolder = folder;

    public Task RunAnalysisAsync() => StartAsync();

    private void RefreshDrives()
    {
        string? keep = SelectedDrive?.Entry.Root;
        Drives.Clear();
        foreach (DriveEntry d in DriveCatalog.List())
        {
            Drives.Add(new DriveRow(d));
        }

        if (_customFolder is null)
        {
            SelectedDrive = Drives.FirstOrDefault(d => d.Entry.Root == keep)
                ?? Drives.FirstOrDefault(d => string.Equals(d.Entry.Root, Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase))
                ?? Drives.FirstOrDefault();
        }
    }

    private void ChooseFolder()
    {
        string? folder = _main.Services.Dialogs.PickFolder(Target);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            CustomFolder = folder;
        }
    }

    private async Task StartAsync()
    {
        string? root = Target;
        if (root is null)
        {
            return;
        }

        IsBusy = true;
        Summary = null;
        Status = "Analisando… você pode cancelar a qualquer momento. Nada está sendo alterado.";
        FilesText = "0";
        BytesText = "0 B";
        FoldersText = "0";
        CurrentFolder = root;
        Elapsed = "00:00";
        _cts = new CancellationTokenSource();
        var progress = new Progress<ScanProgress>(OnProgress);
        try
        {
            ScanResult result = await Task.Run(() => _main.Services.Scanner.Scan(root, new ScanOptions(), progress, _cts.Token));
            _main.PublishScan(result);
            CultureInfo c = CultureInfo.CurrentCulture;
            Summary = result.Completed
                ? string.Create(c, $"Análise concluída em {result.Duration:mm\\:ss}: {SizeText.Format(result.TotalBytes, c)} em {result.FileCount:N0} arquivos e {result.FolderCount:N0} pastas.")
                : string.Create(c, $"Análise cancelada. Resultado PARCIAL: {SizeText.Format(result.TotalBytes, c)} em {result.FileCount:N0} arquivos já vistos (só pastas de primeiro nível).");
            Status = result.Completed ? "Pronto. Veja a Visão geral para entender de onde vem o espaço." : "Cancelada. Os números acima são parciais.";
        }
        catch (DirectoryNotFoundException)
        {
            Status = "Essa pasta não existe mais. Escolha outra.";
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OnProgress(ScanProgress p)
    {
        CultureInfo c = CultureInfo.CurrentCulture;
        FilesText = p.Files.ToString("N0", c);
        FoldersText = p.Directories.ToString("N0", c);
        BytesText = SizeText.Format(p.Bytes, c);
        CurrentFolder = p.CurrentFolder ?? string.Empty;
        Elapsed = p.Elapsed.ToString(@"mm\:ss", c);
    }
}

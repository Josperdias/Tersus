using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Tersus.App.Services;
using Tersus.Core;
using Tersus.Core.Duplicates;

namespace Tersus.App.ViewModels;

public sealed record DupFileRow(string Path, string Name, string Folder, DateTime Modified, bool Sensitive);

public sealed class DupGroupRow
{
    public required DuplicateGroup Group { get; init; }

    public string Header => string.Create(CultureInfo.CurrentCulture, $"{Group.Files.Count} cópias × {SizeText.Format(Group.Size)}");

    public string Potential => string.Create(CultureInfo.CurrentCulture, $"potencial: {SizeText.Format(Group.PotentialBytes)}");

    public string Status => Group.Verified ? "Conteúdo idêntico (SHA-256 verificado)" : "NÃO verificado por completo (mesmo tamanho e mesma amostra)";

    public bool Verified => Group.Verified;

    public IReadOnlyList<DupFileRow> Files { get; init; } = [];
}

public sealed class DuplicatesViewModel : ObservableObject, IPageActivated
{
    private static readonly (string Label, long Bytes)[] MinSizes = [("64 KB ou mais", 64L << 10), ("1 MB ou mais", 1L << 20), ("10 MB ou mais", 10L << 20), ("100 MB ou mais", 100L << 20)];
    private readonly MainViewModel _main;
    private string? _folder;
    private int _minIndex = 1;
    private bool _busy;
    private string _status = "Escolha uma pasta e clique em “Procurar duplicados”. É somente um relatório: o Tersus nunca apaga duplicados.";
    private string _progressText = string.Empty;
    private string _summary = string.Empty;
    private DupGroupRow? _selected;
    private CancellationTokenSource? _cts;

    public DuplicatesViewModel(MainViewModel main)
    {
        _main = main;
        StartCommand = new AsyncCommand(StartAsync, () => !_busy && !string.IsNullOrWhiteSpace(Folder), ex => main.ReportError("Não foi possível procurar duplicados", ex));
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => _busy);
        ChooseFolderCommand = new RelayCommand(() =>
        {
            string? f = main.Services.Dialogs.PickFolder(Folder);
            if (!string.IsNullOrWhiteSpace(f))
            {
                Folder = f;
            }
        }, () => !_busy);
        ExplorerCommand = new RelayCommand(p => ShellLauncher.ShowInExplorer((p as DupFileRow)?.Path));
        main.ScanChanged += (_, _) =>
        {
            if (Folder is null && main.CurrentScan is not null)
            {
                Folder = main.CurrentScan.Root;
            }
        };
    }

    public ObservableCollection<DupGroupRow> Groups { get; } = [];

    public ObservableCollection<string> Warnings { get; } = [];

    public IReadOnlyList<string> MinSizeLabels { get; } = [.. MinSizes.Select(m => m.Label)];

    public string? Folder
    {
        get => _folder;
        set { if (Set(ref _folder, value)) { OnPropertyChanged(nameof(FolderText)); } }
    }

    public string FolderText => Folder ?? "Nenhuma pasta escolhida";

    public int MinIndex { get => _minIndex; set => Set(ref _minIndex, value); }

    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) { OnPropertyChanged(nameof(IsIdle)); } } }

    public bool IsIdle => !_busy;

    public string Status { get => _status; private set => Set(ref _status, value); }

    public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }

    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    public DupGroupRow? SelectedGroup { get => _selected; set => Set(ref _selected, value); }

    public ICommand StartCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand ChooseFolderCommand { get; }

    public ICommand ExplorerCommand { get; }

    public void OnActivated()
    {
        if (Folder is null && _main.CurrentScan is not null)
        {
            Folder = _main.CurrentScan.Root;
        }
    }

    public Task RunAsync() => StartAsync();

    private async Task StartAsync()
    {
        string? folder = Folder;
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        IsBusy = true;
        Groups.Clear();
        Warnings.Clear();
        SelectedGroup = null;
        Summary = string.Empty;
        Status = "Procurando… (somente leitura, você pode cancelar)";
        _cts = new CancellationTokenSource();
        var progress = new Progress<DuplicateProgress>(p =>
            ProgressText = string.Create(CultureInfo.CurrentCulture, $"{p.Stage}: {p.Done:N0}{(p.Total > 0 ? " de " + p.Total.ToString("N0", CultureInfo.CurrentCulture) : string.Empty)}  {p.Current}"));
        var options = new DuplicateOptions { MinFileBytes = MinSizes[Math.Clamp(MinIndex, 0, MinSizes.Length - 1)].Bytes };
        var fs = _main.Services.Cleanup.FileSystem;
        try
        {
            DuplicateResult result = await Task.Run(() =>
                new DuplicateAnalyzer(p => fs.Probe(p).Identity).Find(folder, options, progress, _cts.Token));

            foreach (DuplicateGroup g in result.Groups.Take(1000))
            {
                Groups.Add(new DupGroupRow
                {
                    Group = g,
                    Files = [.. g.Files.Select(f => new DupFileRow(f.Path, System.IO.Path.GetFileName(f.Path), System.IO.Path.GetDirectoryName(f.Path) ?? string.Empty, f.LastWriteUtc, f.InSensitiveLocation))],
                });
            }

            foreach (string w in result.Warnings)
            {
                Warnings.Add(w);
            }

            if (result.Groups.Count > 1000)
            {
                Warnings.Add($"Mostrando os 1.000 maiores grupos de {result.Groups.Count:N0}.");
            }

            CultureInfo c = CultureInfo.CurrentCulture;
            string head = string.Create(c, $"{result.Groups.Count:N0} grupo(s). Potencial (verificado): {SizeText.Format(result.VerifiedPotentialBytes, c)}");
            string unverified = result.UnverifiedPotentialBytes > 0 ? "  •  não verificado: " + SizeText.Format(result.UnverifiedPotentialBytes, c) : string.Empty;
            Summary = result.Groups.Count == 0
                ? "Nenhum duplicado encontrado com os critérios escolhidos."
                : head + unverified + ". “Potencial” é o que sobraria se mantivesse uma cópia de cada, NÃO espaço já liberado.";
            Status = result.Completed ? "Concluído." : "Cancelado: resultado parcial.";
            SelectedGroup = Groups.FirstOrDefault();
        }
        catch (DirectoryNotFoundException)
        {
            Status = "Essa pasta não existe mais.";
        }
        finally
        {
            IsBusy = false;
            ProgressText = string.Empty;
            _cts?.Dispose();
            _cts = null;
        }
    }
}

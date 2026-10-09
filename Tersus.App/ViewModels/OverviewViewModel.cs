using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Media;
using Tersus.App.Controls;
using Tersus.App.Services;
using Tersus.Core;
using Tersus.Core.Knowledge;
using Tersus.Core.Scanning;
using ByteSize = Tersus.Core.SizeText;

namespace Tersus.App.ViewModels;

public sealed class FolderRow
{
    public SizeNode? Node { get; init; }

    public required string Name { get; init; }

    public long Bytes { get; init; }

    public double Fraction { get; init; }

    public string Detail { get; init; } = string.Empty;

    public bool IsOther { get; init; }

    public bool CanOpen { get; init; }

    public Brush Color { get; init; } = Brushes.Gray;

    public string Key { get; init; } = string.Empty;

    public string SizeText => ByteSize.Format(Bytes);

    public string PercentText => Fraction.ToString("P1", CultureInfo.CurrentCulture);
}

public sealed record BreadcrumbItem(string Label, SizeNode Node);

public sealed record CategoryRow(string Label, long Bytes, double Fraction)
{
    public string SizeText => ByteSize.Format(Bytes);

    public string PercentText => Fraction.ToString("P1", CultureInfo.CurrentCulture);
}

public sealed class OverviewViewModel : ObservableObject, IPageActivated
{
    private const int MaxRows = 40;
    private readonly MainViewModel _main;
    private SizeNode? _current;
    private FolderRow? _selected;
    private string _title = string.Empty;
    private string _subtitle = string.Empty;
    private IReadOnlyList<TreemapEntry> _treemap = [];
    private Explanation? _explanation;

    public OverviewViewModel(MainViewModel main)
    {
        _main = main;
        OpenCommand = new RelayCommand(p => Open(p as FolderRow), p => (p as FolderRow)?.CanOpen == true);
        UpCommand = new RelayCommand(() => GoUp(), () => _current?.Parent is not null);
        GoToCommand = new RelayCommand(p => { if (p is BreadcrumbItem b) { Show(b.Node); } });
        ExplorerCommand = new RelayCommand(() => ShellLauncher.ShowInExplorer(SelectedPath ?? _current?.FullPath), () => _current is not null);
        TreemapSelectedCommand = new RelayCommand(p => SelectByKey(p as string));
        TreemapInvokedCommand = new RelayCommand(p => OnTreemapInvoked(p as string));
        GoHomeCommand = new RelayCommand(() => main.Navigate("home"));
        main.ScanChanged += (_, _) => Reload();
        Reload();
    }

    public ObservableCollection<FolderRow> Rows { get; } = [];

    public ObservableCollection<BreadcrumbItem> Breadcrumbs { get; } = [];

    public ObservableCollection<CategoryRow> Categories { get; } = [];

    public ObservableCollection<string> Notes { get; } = [];

    public bool HasScan => _main.CurrentScan is not null;

    public string Title { get => _title; private set => Set(ref _title, value); }

    public string Subtitle { get => _subtitle; private set => Set(ref _subtitle, value); }

    public IReadOnlyList<TreemapEntry> TreemapItems { get => _treemap; private set => Set(ref _treemap, value); }

    public FolderRow? SelectedRow
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                Explanation = value is null ? null : ExplainRow(value);
                OnPropertyChanged(nameof(SelectedPath));
                OnPropertyChanged(nameof(SelectedKey));
            }
        }
    }

    public Explanation? Explanation { get => _explanation; private set => Set(ref _explanation, value); }

    public string? SelectedPath => _selected?.Node?.FullPath;

    public string? SelectedKey => _selected?.Key;

    public ICommand OpenCommand { get; }

    public ICommand UpCommand { get; }

    public ICommand GoToCommand { get; }

    public ICommand ExplorerCommand { get; }

    public ICommand TreemapSelectedCommand { get; }

    public ICommand TreemapInvokedCommand { get; }

    public ICommand GoHomeCommand { get; }

    public void OnActivated()
    {
    }

    private void Reload()
    {
        OnPropertyChanged(nameof(HasScan));
        ScanResult? scan = _main.CurrentScan;
        Notes.Clear();
        Categories.Clear();
        if (scan is null)
        {
            _current = null;
            Rows.Clear();
            Breadcrumbs.Clear();
            TreemapItems = [];
            Title = string.Empty;
            Subtitle = string.Empty;
            return;
        }

        BuildNotes(scan);
        BuildCategories(scan);
        Show(scan.Tree);
    }

    private void Show(SizeNode node)
    {
        _current = node;
        CultureInfo c = CultureInfo.CurrentCulture;
        Title = node.FullPath;
        Subtitle = string.Create(c, $"{SizeText.Format(node.Bytes, c)}  •  {Plural.Files(node.FileCount, c)}  •  {Plural.Folders(node.FolderCount, c)}");

        Breadcrumbs.Clear();
        var chain = new List<SizeNode>();
        for (SizeNode? n = node; n is not null; n = n.Parent)
        {
            chain.Add(n);
        }

        chain.Reverse();
        foreach (SizeNode n in chain)
        {
            Breadcrumbs.Add(new BreadcrumbItem(n.Parent is null ? n.Name : System.IO.Path.GetFileName(n.Name.TrimEnd('\\', '/')) is { Length: > 0 } f ? f : n.Name, n));
        }

        Rows.Clear();
        var entries = new List<TreemapEntry>();
        double total = Math.Max(1, node.Bytes);
        int index = 0;
        long shown = 0;
        foreach (SizeNode child in node.Children.Take(MaxRows))
        {
            string key = index.ToString(CultureInfo.InvariantCulture);
            Rows.Add(new FolderRow
            {
                Node = child,
                Name = child.Name,
                Bytes = child.Bytes,
                Fraction = child.Bytes / total,
                Detail = string.Create(c, $"{Plural.Files(child.FileCount, c)} • {Plural.Folders(child.FolderCount, c)}"),
                CanOpen = child.Children.Count > 0,
                Color = Palette.For(index),
                Key = key,
            });
            entries.Add(new TreemapEntry(key, child.Name, child.Bytes, IsOther: false));
            shown += child.Bytes;
            index++;
        }

        if (node.DirectFilesBytes > 0)
        {
            Rows.Add(new FolderRow
            {
                Name = "(arquivos soltos nesta pasta)",
                Bytes = node.DirectFilesBytes,
                Fraction = node.DirectFilesBytes / total,
                Detail = "Arquivos que estão diretamente aqui, fora das subpastas.",
                IsOther = true,
                Color = Palette.For(0, isOther: true),
                Key = "files",
            });
            entries.Add(new TreemapEntry("files", "(arquivos soltos)", node.DirectFilesBytes, IsOther: true));
            shown += node.DirectFilesBytes;
        }

        long rest = node.Bytes - shown;
        if (rest > 0)
        {
            Rows.Add(new FolderRow
            {
                Name = "(pastas menores agrupadas)",
                Bytes = rest,
                Fraction = rest / total,
                Detail = "Muitas pastas pequenas, somadas para o mapa ficar legível.",
                IsOther = true,
                Color = Palette.For(0, isOther: true),
                Key = "rest",
            });
            entries.Add(new TreemapEntry("rest", "(menores)", rest, IsOther: true));
        }

        TreemapItems = entries;
        SelectedRow = Rows.FirstOrDefault();
    }

    private void Open(FolderRow? row)
    {
        if (row?.Node is { Children.Count: > 0 } node)
        {
            Show(node);
        }
    }

    private void GoUp()
    {
        if (_current?.Parent is { } parent)
        {
            Show(parent);
        }
    }

    private void SelectByKey(string? key)
    {
        FolderRow? row = Rows.FirstOrDefault(r => r.Key == key);
        if (row is not null)
        {
            SelectedRow = row;
        }
    }

    private void OnTreemapInvoked(string? key)
    {
        SelectByKey(key);
        if (_selected is { CanOpen: true } row)
        {
            Open(row);
        }
    }

    private static Explanation ExplainRow(FolderRow row)
    {
        if (row.Node is null)
        {
            return new Explanation(row.Name, row.Detail, "—", RiskLevel.Info, "—", Confidence.High,
                "Use “Maiores arquivos” para ver quais arquivos pesam mais aqui.");
        }

        return FileExplainer.Explain(row.Node.FullPath, isDirectory: true);
    }

    private void BuildNotes(ScanResult scan)
    {
        CultureInfo c = CultureInfo.CurrentCulture;
        Notes.Add("Os tamanhos são LÓGICOS (soma do tamanho dos arquivos). Hardlinks, compressão e arquivos esparsos podem fazer o uso físico no disco ser diferente.");
        if (!scan.Completed)
        {
            Notes.Add("A análise foi cancelada: os números são PARCIAIS e só as pastas de primeiro nível são mostradas.");
        }

        if (scan.LinksSkipped > 0)
        {
            Notes.Add(string.Create(c, $"{scan.LinksSkipped:N0} link(s)/junção(ões) não foram seguidos, para não contar o mesmo dado duas vezes."));
        }

        if (scan.InaccessibleFolders > 0)
        {
            string example = scan.InaccessibleSamples.Count > 0 ? $" Ex.: {scan.InaccessibleSamples[0]}" : string.Empty;
            Notes.Add(string.Create(c, $"{scan.InaccessibleFolders:N0} pasta(s) sem permissão de leitura foram ignoradas.{example}"));
        }

        if (scan.CloudOnlyFiles > 0)
        {
            Notes.Add(string.Create(c, $"{scan.CloudOnlyFiles:N0} arquivo(s) existem só na nuvem ({SizeText.Format(scan.CloudOnlyBytes, c)} lógicos): não ocupam espaço local e ficam fora dos totais."));
        }

        if (scan.UnaccountedBytes is long gap && scan.VolumeTotalBytes is long total && scan.VolumeFreeBytes is long free)
        {
            Notes.Add(string.Create(c, $"O Windows informa {SizeText.Format(total - free, c)} em uso na unidade; a análise somou {SizeText.Format(scan.TotalBytes, c)}. Diferença de {SizeText.Format(gap, c)} (arquivos de sistema, outros usuários, pontos de restauração, áreas sem permissão)."));
        }

        foreach (string n in scan.Notes)
        {
            Notes.Add(n);
        }
    }

    private void BuildCategories(ScanResult scan)
    {
        long total = Math.Max(1, scan.Extensions.Sum(e => e.Bytes));
        var byCategory = new Dictionary<FileCategory, long>();
        foreach (ExtensionStat e in scan.Extensions)
        {
            FileCategory cat = e.Extension == "(outras)" ? FileCategory.Other : FileCategories.Of(e.Extension);
            byCategory[cat] = byCategory.GetValueOrDefault(cat) + e.Bytes;
        }

        foreach ((FileCategory cat, long bytes) in byCategory.OrderByDescending(kv => kv.Value).Take(10))
        {
            Categories.Add(new CategoryRow(FileCategories.Label(cat), bytes, (double)bytes / total));
        }
    }
}

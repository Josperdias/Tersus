using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Input;
using Tersus.App.Services;
using Tersus.Core;
using Tersus.Core.Knowledge;
using Tersus.Core.Scanning;
using Tersus.Core.Windows;

namespace Tersus.App.ViewModels;

public sealed class FileRow
{
    public required FileEntry Entry { get; init; }

    public string Name => Entry.Name;

    public string Folder => Entry.Folder;

    public long Bytes => Entry.Bytes;

    public DateTime Modified => Entry.LastWriteUtc;

    public FileCategory Category { get; init; }

    public string CategoryLabel => FileCategories.Label(Category);
}

public sealed class LargeFilesViewModel : ObservableObject, IPageActivated
{
    private static readonly (string Label, long Bytes)[] MinSizes = [("Qualquer tamanho", 0), ("10 MB ou mais", 10L << 20), ("100 MB ou mais", 100L << 20), ("1 GB ou mais", 1L << 30)];
    private readonly MainViewModel _main;
    private readonly ObservableCollection<FileRow> _all = [];
    private string _search = string.Empty;
    private string _category = AllCategories;
    private int _minSizeIndex;
    private FileRow? _selected;
    private Explanation? _explanation;
    private string? _allocatedText;

    private const string AllCategories = "Todos os tipos";

    public LargeFilesViewModel(MainViewModel main)
    {
        _main = main;
        View = CollectionViewSource.GetDefaultView(_all);
        View.Filter = Matches;
        ExplorerCommand = new RelayCommand(() => ShellLauncher.ShowInExplorer(_selected?.Entry.Path), () => _selected is not null);
        CopyPathCommand = new RelayCommand(() => { if (_selected is not null) { System.Windows.Clipboard.SetText(_selected.Entry.Path); } }, () => _selected is not null);
        GoHomeCommand = new RelayCommand(() => main.Navigate("home"));
        main.ScanChanged += (_, _) => Reload();
        Reload();
    }

    public ICollectionView View { get; }

    public IReadOnlyList<string> Categories { get; } = [AllCategories, .. Enum.GetValues<FileCategory>().Select(FileCategories.Label).Distinct()];

    public IReadOnlyList<string> MinSizeLabels { get; } = [.. MinSizes.Select(m => m.Label)];

    public bool HasScan => _main.CurrentScan is not null;

    public string Summary { get; private set; } = string.Empty;

    public string SearchText
    {
        get => _search;
        set { if (Set(ref _search, value)) { Refresh(); } }
    }

    public string SelectedCategory
    {
        get => _category;
        set { if (Set(ref _category, value)) { Refresh(); } }
    }

    public int MinSizeIndex
    {
        get => _minSizeIndex;
        set { if (Set(ref _minSizeIndex, value)) { Refresh(); } }
    }

    public FileRow? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                Explanation = value is null ? null : FileExplainer.Explain(value.Entry.Path, isDirectory: false);
                AllocatedText = value is null ? null : DescribeAllocated(value);
            }
        }
    }

    public Explanation? Explanation { get => _explanation; private set => Set(ref _explanation, value); }

    public string? AllocatedText { get => _allocatedText; private set => Set(ref _allocatedText, value); }

    public ICommand ExplorerCommand { get; }

    public ICommand CopyPathCommand { get; }

    public ICommand GoHomeCommand { get; }

    public void OnActivated()
    {
    }

    private void Reload()
    {
        _all.Clear();
        ScanResult? scan = _main.CurrentScan;
        OnPropertyChanged(nameof(HasScan));
        if (scan is not null)
        {
            foreach (FileEntry f in scan.LargestFiles)
            {
                _all.Add(new FileRow { Entry = f, Category = FileCategories.Of(f.Extension) });
            }
        }

        Refresh();
    }

    private void Refresh()
    {
        View.Refresh();
        int shown = View.Cast<object>().Count();
        Summary = string.Create(CultureInfo.CurrentCulture, $"{shown:N0} de {_all.Count:N0} arquivos listados (os maiores da análise).");
        OnPropertyChanged(nameof(Summary));
    }

    private bool Matches(object o)
    {
        if (o is not FileRow r)
        {
            return false;
        }

        if (r.Bytes < MinSizes[Math.Clamp(_minSizeIndex, 0, MinSizes.Length - 1)].Bytes)
        {
            return false;
        }

        if (_category != AllCategories && r.CategoryLabel != _category)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(_search)
            || r.Entry.Path.Contains(_search.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string? DescribeAllocated(FileRow row)
    {
        try
        {
            long? allocated = WindowsFileSystem.AllocatedSize(row.Entry.Path);
            if (allocated is null)
            {
                return null;
            }

            CultureInfo c = CultureInfo.CurrentCulture;
            string text = $"Tamanho lógico: {SizeText.Format(row.Bytes, c)}  •  Alocado no disco: {SizeText.Format(allocated.Value, c)}";
            return allocated < row.Bytes ? text + " (arquivo comprimido ou esparso: ocupa menos do que mostra)" : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

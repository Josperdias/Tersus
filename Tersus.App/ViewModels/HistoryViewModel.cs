using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Tersus.App.Services;
using Tersus.Core.History;
using ByteSize = Tersus.Core.SizeText;

namespace Tersus.App.ViewModels;

public sealed class SnapshotRow(Snapshot snapshot, double fraction, string deltaText)
{
    public Snapshot Snapshot { get; } = snapshot;

    public DateTime When => Snapshot.CreatedUtc.ToLocalTime();

    public string WhenText => When.ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture);

    public string Root => Snapshot.Root;

    public long Bytes => Snapshot.TotalBytes;

    public string SizeText => ByteSize.Format(Snapshot.TotalBytes);

    public string FilesText => Plural.Files(Snapshot.FileCount);

    public double Fraction { get; } = fraction;

    public string DeltaText { get; } = deltaText;
}

public sealed class DeltaRow(FolderDelta delta)
{
    public string Name => delta.Name;

    public string BeforeText => ByteSize.Format(delta.BeforeBytes);

    public string AfterText => ByteSize.Format(delta.AfterBytes);

    public long Delta => delta.Delta;

    public string DeltaText => (delta.Delta >= 0 ? "+" : "−") + ByteSize.Format(Math.Abs(delta.Delta));

    public string KindText => delta.Kind switch
    {
        DeltaKind.Grew => "cresceu",
        DeltaKind.Shrunk => "diminuiu",
        DeltaKind.New => "nova",
        DeltaKind.Removed => "sumiu",
        _ => "igual",
    };
}

/// <summary>Local history of completed analyses (at most 24 compact summaries, never file names), compared with the previous one of the same location.</summary>
public sealed class HistoryViewModel : ObservableObject, IPageActivated
{
    private readonly MainViewModel _main;
    private IReadOnlyList<Snapshot> _all = [];
    private string? _selectedRoot;
    private SnapshotRow? _selected;
    private string _comparisonHeadline = string.Empty;
    private string _emptyText = string.Empty;

    public HistoryViewModel(MainViewModel main)
    {
        _main = main;
        ClearCommand = new RelayCommand(Clear, () => _all.Count > 0);
        GoHomeCommand = new RelayCommand(() => main.Navigate("home"));
        main.ScanChanged += (_, _) => Reload();
    }

    public ObservableCollection<string> Roots { get; } = [];

    public ObservableCollection<SnapshotRow> Rows { get; } = [];

    public ObservableCollection<DeltaRow> Deltas { get; } = [];

    public string? SelectedRoot
    {
        get => _selectedRoot;
        set
        {
            if (Set(ref _selectedRoot, value))
            {
                BuildRows();
            }
        }
    }

    public SnapshotRow? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                BuildComparison();
            }
        }
    }

    public bool HasData => _all.Count > 0;

    public string EmptyText { get => _emptyText; private set => Set(ref _emptyText, value); }

    public string ComparisonHeadline { get => _comparisonHeadline; private set => Set(ref _comparisonHeadline, value); }

    public bool HasDeltas => Deltas.Count > 0;

    public ICommand ClearCommand { get; }

    public ICommand GoHomeCommand { get; }

    public void OnActivated() => Reload();

    public void Reload()
    {
        _all = _main.Services.History.Load();
        OnPropertyChanged(nameof(HasData));
        EmptyText = "Ainda não há histórico. Cada análise COMPLETA que você faz é resumida aqui (totais e pastas de primeiro nível, sem nomes de arquivos) para mostrar como o disco cresceu. Fica só neste computador e guarda no máximo 24 resumos.";
        string? keep = _selectedRoot;
        Roots.Clear();
        foreach (string r in _all.Select(s => s.Root).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(r => r, StringComparer.OrdinalIgnoreCase))
        {
            Roots.Add(r);
        }

        _selectedRoot = Roots.FirstOrDefault(r => HistoryStore.SameRoot(r, keep ?? string.Empty))
            ?? (_main.CurrentScan is { } scan ? Roots.FirstOrDefault(r => HistoryStore.SameRoot(r, scan.Root)) : null)
            ?? Roots.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedRoot));
        BuildRows();
    }

    private void BuildRows()
    {
        Rows.Clear();
        Deltas.Clear();
        OnPropertyChanged(nameof(HasDeltas));
        ComparisonHeadline = string.Empty;
        if (_selectedRoot is null)
        {
            Selected = null;
            return;
        }

        List<Snapshot> list = [.. _all.Where(s => HistoryStore.SameRoot(s.Root, _selectedRoot)).OrderBy(s => s.CreatedUtc)];
        long max = Math.Max(1, list.Count == 0 ? 1 : list.Max(s => s.TotalBytes));
        Snapshot? previous = null;
        var rows = new List<SnapshotRow>();
        foreach (Snapshot s in list)
        {
            string delta = previous is null ? "primeira análise" : (s.TotalBytes >= previous.TotalBytes ? "+" : "−") + ByteSize.Format(Math.Abs(s.TotalBytes - previous.TotalBytes)) + " desde a anterior";
            rows.Add(new SnapshotRow(s, (double)s.TotalBytes / max, delta));
            previous = s;
        }

        rows.Reverse();
        foreach (SnapshotRow r in rows)
        {
            Rows.Add(r);
        }

        Selected = Rows.FirstOrDefault();
    }

    private void BuildComparison()
    {
        Deltas.Clear();
        ComparisonHeadline = string.Empty;
        if (_selected is null)
        {
            OnPropertyChanged(nameof(HasDeltas));
            return;
        }

        SnapshotComparison? cmp = HistoryStore.CompareWithPrevious(_all, _selected.Snapshot);
        if (cmp is null)
        {
            ComparisonHeadline = "Esta é a primeira análise guardada deste local; não há com o que comparar ainda.";
            OnPropertyChanged(nameof(HasDeltas));
            return;
        }

        CultureInfo c = CultureInfo.CurrentCulture;
        foreach (FolderDelta d in cmp.Folders.Where(f => f.Kind != DeltaKind.Same).Take(15))
        {
            Deltas.Add(new DeltaRow(d));
        }

        string change = cmp.TotalDelta == 0
            ? "não mudou de tamanho"
            : string.Create(c, $"{(cmp.TotalDelta > 0 ? "cresceu" : "diminuiu")} {ByteSize.Format(Math.Abs(cmp.TotalDelta), c)}");
        ComparisonHeadline = string.Create(c, $"Desde a análise anterior (há {ElapsedText(cmp.Elapsed)}), este local {change} (tamanho lógico).")
            + (Deltas.Count > 0 ? " Pastas que mais mudaram:" : string.Empty);

        OnPropertyChanged(nameof(HasDeltas));
    }

    private static string ElapsedText(TimeSpan elapsed) =>
        elapsed.TotalMinutes < 1 ? "menos de 1 minuto"
        : elapsed.TotalHours < 1 ? Plural.Of((long)elapsed.TotalMinutes, "minuto", "minutos")
        : elapsed.TotalDays < 1 ? Plural.Of((long)elapsed.TotalHours, "hora", "horas")
        : Plural.Of((long)Math.Round(elapsed.TotalDays), "dia", "dias");

    private void Clear()
    {
        if (!_main.Services.Dialogs.Confirm(
                "Apagar o histórico do Tersus?",
                "Isso apaga só os resumos que o Tersus guardou sobre as análises (o arquivo historico.json). Nenhum arquivo seu é tocado. Você pode continuar usando o programa normalmente.",
                "Apagar histórico"))
        {
            return;
        }

        _main.Services.History.Clear();
        Reload();
    }
}

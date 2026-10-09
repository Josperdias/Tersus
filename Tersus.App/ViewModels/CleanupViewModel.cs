using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Input;
using Tersus.App.Services;
using Tersus.Core.Cleanup;
using Tersus.Core.Policy;
using Tersus.Core.Storage;
using Tersus.Core.Windows;
using ByteSize = Tersus.Core.SizeText;

namespace Tersus.App.ViewModels;

public enum CleanupMode
{
    Quick,
    Custom,
    Goal,
}

public enum CleanupStage
{
    Start,
    Searching,
    Review,
    Simulated,
    Executing,
    Done,
}

public sealed class CandidateRow(CleanupCandidate candidate, Action<CandidateRow> changed) : ObservableObject
{
    private bool _selected;

    public CleanupCandidate Candidate { get; } = candidate;

    public string Name => Candidate.Name;

    // The folder inside TEMP (the file name has its own column).
    public string Where => System.IO.Path.GetDirectoryName(Candidate.RelativePath) is { Length: > 0 } folder ? folder : "(raiz da pasta TEMP)";

    public long Bytes => Candidate.Length;

    public string SizeText => ByteSize.Format(Candidate.Length);

    public int AgeDays => Candidate.AgeDays;

    public string AgeText => string.Create(CultureInfo.CurrentCulture, $"{Candidate.AgeDays:N0} dias");

    public DateTime Modified => Candidate.ModifiedUtc.ToLocalTime();

    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                changed(this);
            }
        }
    }

    /// <summary>Bulk changes: the owner recomputes its totals once afterwards instead of once per row.</summary>
    internal void SetQuietly(bool value)
    {
        if (_selected != value)
        {
            _selected = value;
            OnPropertyChanged(nameof(IsSelected));
        }
    }
}

public sealed record ReasonRow(string Text, int Count)
{
    public string CountText => Count.ToString("N0", CultureInfo.CurrentCulture);
}

public sealed class ResultRow(CleanupItemResult result)
{
    public string Name => System.IO.Path.GetFileName(result.Path);

    public string Path => result.Path;

    public string SizeText => ByteSize.Format(result.Length);

    public bool IsProblem => result.Outcome is CleanupItemOutcome.Failed or CleanupItemOutcome.UnprovenPossiblePermanentDeletion;

    public string OutcomeText => result.Outcome switch
    {
        CleanupItemOutcome.MovedToRecycleBin => "Enviado à Lixeira",
        CleanupItemOutcome.Skipped => "Ignorado",
        CleanupItemOutcome.Failed => "Falhou (o arquivo continua onde estava)",
        _ => "VERIFIQUE A LIXEIRA: o arquivo sumiu sem confirmação",
    };

    public string Message => result.Message;
}

public sealed class CleanupViewModel : ObservableObject, IPageActivated
{
    private const int MaxResultRows = 2000;
    private readonly MainViewModel _main;
    private readonly CleanupServices.Stack _stack;
    private readonly List<CleanupCandidate> _all = [];
    private CleanupStage _stage = CleanupStage.Start;
    private CleanupMode _mode = CleanupMode.Quick;
    private CleanupPlan? _plan;
    private CleanupReport? _report;
    private CancellationTokenSource? _cts;
    private bool _suspend;
    private string _goalText = "500";
    private int _goalUnit;
    private string _goalMessage = string.Empty;
    private int _selectedCount;
    private long _selectedBytes;
    private string _progressText = string.Empty;
    private double _progressValue;
    private double _progressMax = 1;
    private bool _progressIndeterminate = true;
    private string _searchSummary = string.Empty;
    private string _planHeadline = string.Empty;
    private string _planDetail = string.Empty;
    private string _planNote = string.Empty;
    private string _reportHeadline = string.Empty;
    private string _reportDetail = string.Empty;
    private string _reportSpace = string.Empty;
    private string _reportPreflight = string.Empty;
    private string? _reportWarning;
    private string? _logPath;
    private int _sortIndex;

    public CleanupViewModel(MainViewModel main)
    {
        _main = main;
        _stack = main.Services.Cleanup;
        CandidatesView = CollectionViewSource.GetDefaultView(Candidates);
        ApplySort();

        SearchCommand = new AsyncCommand(SearchAsync, () => IsEnabled && Stage is CleanupStage.Start or CleanupStage.Review or CleanupStage.Simulated or CleanupStage.Done, ex => main.ReportError("Não foi possível procurar temporários", ex));
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => Stage is CleanupStage.Searching or CleanupStage.Executing);
        SelectAllCommand = new RelayCommand(() => { SetMode(CleanupMode.Custom); SetSelection(_all); }, () => Stage is CleanupStage.Review or CleanupStage.Simulated);
        SelectNoneCommand = new RelayCommand(() => { SetMode(CleanupMode.Custom); SetSelection([]); }, () => Stage is CleanupStage.Review or CleanupStage.Simulated);
        SimulateCommand = new AsyncCommand(SimulateAsync, () => Stage == CleanupStage.Review && SelectedCount > 0, ex => main.ReportError("Não foi possível simular", ex));
        BackCommand = new RelayCommand(InvalidatePlan, () => Stage == CleanupStage.Simulated);
        RecycleCommand = new AsyncCommand(RecycleAsync, () => Stage == CleanupStage.Simulated && _plan is { Approved.Count: > 0 }, OnExecutionError);
        RestartCommand = new RelayCommand(ResetToStart, () => Stage == CleanupStage.Done);
        OpenRecycleBinCommand = new RelayCommand(ShellLauncher.OpenRecycleBin);
        OpenLogFolderCommand = new RelayCommand(() => ShellLauncher.OpenFolder(_main.Services.Paths.LogsDirectory));
    }

    public bool IsEnabled => _stack.Policy is not null;

    public string DisabledReason => _stack.Problem ?? "Não foi possível identificar com segurança a pasta TEMP do seu usuário.";

    public string RulesLine => _stack.Policy is { } p ? SafetyTexts.RulesLine(p) : string.Empty;

    public string TempFolderText => _stack.Policy?.TempRoot ?? _stack.TempFolder;

    public IReadOnlyList<string> NeverList => SafetyTexts.NeverList;

    public ObservableCollection<CandidateRow> Candidates { get; } = [];

    public ICollectionView CandidatesView { get; }

    public ObservableCollection<ReasonRow> FoundRefusals { get; } = [];

    public ObservableCollection<ReasonRow> PlanRefusals { get; } = [];

    public ObservableCollection<ResultRow> Results { get; } = [];

    public IReadOnlyList<string> SortLabels { get; } = ["Maiores primeiro", "Mais antigos primeiro", "Nome"];

    public IReadOnlyList<string> GoalUnits { get; } = ["MB", "GB"];

    public CleanupStage Stage
    {
        get => _stage;
        private set
        {
            if (Set(ref _stage, value))
            {
                foreach (string n in new[] { nameof(IsStart), nameof(IsSearching), nameof(IsReviewing), nameof(IsSimulated), nameof(IsExecuting), nameof(IsDone), nameof(CanEditSelection) })
                {
                    OnPropertyChanged(n);
                }
            }
        }
    }

    public bool IsStart => Stage == CleanupStage.Start;

    public bool IsSearching => Stage == CleanupStage.Searching;

    public bool IsReviewing => Stage is CleanupStage.Review or CleanupStage.Simulated;

    public bool IsSimulated => Stage == CleanupStage.Simulated;

    public bool IsExecuting => Stage == CleanupStage.Executing;

    public bool IsDone => Stage == CleanupStage.Done;

    public bool CanEditSelection => Stage == CleanupStage.Review;

    public bool IsQuick { get => _mode == CleanupMode.Quick; set { if (value) { SetMode(CleanupMode.Quick, apply: true); } } }

    public bool IsCustom { get => _mode == CleanupMode.Custom; set { if (value) { SetMode(CleanupMode.Custom, apply: true); } } }

    public bool IsGoal { get => _mode == CleanupMode.Goal; set { if (value) { SetMode(CleanupMode.Goal, apply: true); } } }

    public string GoalText
    {
        get => _goalText;
        set
        {
            if (Set(ref _goalText, value) && _mode == CleanupMode.Goal)
            {
                ApplyGoal();
            }
        }
    }

    public int GoalUnitIndex
    {
        get => _goalUnit;
        set
        {
            if (Set(ref _goalUnit, value) && _mode == CleanupMode.Goal)
            {
                ApplyGoal();
            }
        }
    }

    public string GoalMessage { get => _goalMessage; private set => Set(ref _goalMessage, value); }

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

    public int SelectedCount { get => _selectedCount; private set => Set(ref _selectedCount, value); }

    public long SelectedBytes { get => _selectedBytes; private set => Set(ref _selectedBytes, value); }

    public string SelectionText => string.Create(CultureInfo.CurrentCulture, $"{SelectedCount:N0} de {_all.Count:N0} selecionado(s) — {ByteSize.Format(SelectedBytes)} (tamanho lógico)");

    public string SearchSummary { get => _searchSummary; private set => Set(ref _searchSummary, value); }

    public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }

    public double ProgressValue { get => _progressValue; private set => Set(ref _progressValue, value); }

    public double ProgressMax { get => _progressMax; private set => Set(ref _progressMax, value); }

    public bool ProgressIndeterminate { get => _progressIndeterminate; private set => Set(ref _progressIndeterminate, value); }

    public string PlanHeadline { get => _planHeadline; private set => Set(ref _planHeadline, value); }

    public string PlanDetail { get => _planDetail; private set => Set(ref _planDetail, value); }

    public string PlanNote { get => _planNote; private set => Set(ref _planNote, value); }

    public bool HasPlanRefusals => PlanRefusals.Count > 0;

    public bool CanRecycle => Stage == CleanupStage.Simulated && _plan is { Approved.Count: > 0 };

    public string ReportHeadline { get => _reportHeadline; private set => Set(ref _reportHeadline, value); }

    public string ReportDetail { get => _reportDetail; private set => Set(ref _reportDetail, value); }

    public string ReportSpace { get => _reportSpace; private set => Set(ref _reportSpace, value); }

    /// <summary>What the Recycle Bin check did. It sends one tiny file of its own to the bin to prove the bin works; that file stays there.</summary>
    public string ReportPreflight { get => _reportPreflight; private set => Set(ref _reportPreflight, value); }

    public string? ReportWarning
    {
        get => _reportWarning;
        private set
        {
            if (Set(ref _reportWarning, value))
            {
                OnPropertyChanged(nameof(HasReportWarning));
            }
        }
    }

    public bool HasReportWarning => _reportWarning is not null;

    public string? LogPath { get => _logPath; private set { Set(ref _logPath, value); OnPropertyChanged(nameof(HasLog)); } }

    public bool HasLog => _logPath is not null;

    public CleanupReport? Report => _report;

    public CleanupPlan? Plan => _plan;

    public ICommand SearchCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand SelectAllCommand { get; }

    public ICommand SelectNoneCommand { get; }

    public ICommand SimulateCommand { get; }

    public ICommand BackCommand { get; }

    public ICommand RecycleCommand { get; }

    public ICommand RestartCommand { get; }

    public ICommand OpenRecycleBinCommand { get; }

    public ICommand OpenLogFolderCommand { get; }

    public void OnActivated()
    {
    }

    // ---- search -------------------------------------------------------------------------------------------------------

    public async Task SearchAsync()
    {
        if (_stack.Policy is not { } policy)
        {
            return;
        }

        ResetResults();
        Stage = CleanupStage.Searching;
        ProgressIndeterminate = true;
        ProgressText = "Procurando… (somente leitura)";
        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;
        var progress = new Progress<CandidateScanProgress>(p =>
            ProgressText = string.Create(CultureInfo.CurrentCulture, $"Procurando… {p.FilesVisited:N0} itens verificados, {p.Eligible:N0} elegíveis"));
        string? start = _main.Services.CleanupStartFolder;
        try
        {
            CandidateScanResult result = await Task.Run(() => new CleanupCandidateScanner(policy, _stack.FileSystem).Scan(progress, token, startFolder: start));
            ApplySearch(result);
        }
        catch
        {
            Stage = CleanupStage.Start;
            throw;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void ApplySearch(CandidateScanResult result)
    {
        CultureInfo c = CultureInfo.CurrentCulture;
        _all.Clear();
        _all.AddRange(result.Candidates);
        _suspend = true;
        try
        {
            Candidates.Clear();
            foreach (CleanupCandidate cand in result.Candidates)
            {
                Candidates.Add(new CandidateRow(cand, OnRowChanged));
            }
        }
        finally
        {
            _suspend = false;
        }

        FoundRefusals.Clear();
        foreach ((RefusalReason reason, int count) in result.Stats.Refused.OrderByDescending(kv => kv.Value))
        {
            FoundRefusals.Add(new ReasonRow(RefusalText.Describe(reason), count));
        }

        var parts = new List<string>
        {
            string.Create(c, $"{result.Stats.TempFilesSeen:N0} arquivo(s) .tmp/.temp encontrados em {result.TempRoot}; {result.Stats.Eligible:N0} passaram em todas as verificações ({ByteSize.Format(result.Stats.EligibleBytes, c)})."),
        };
        if (!result.Completed)
        {
            parts.Add("A busca foi interrompida: a lista é PARCIAL.");
        }

        if (result.Stats.Truncated)
        {
            parts.Add("Há mais arquivos elegíveis do que o limite da lista; o restante só aparecerá em uma próxima busca.");
        }

        if (result.Stats.InaccessibleFolders > 0)
        {
            parts.Add(string.Create(c, $"{result.Stats.InaccessibleFolders:N0} pasta(s) sem permissão foram ignoradas."));
        }

        if (result.Stats.LinksSkipped > 0)
        {
            parts.Add(string.Create(c, $"{result.Stats.LinksSkipped:N0} link(s)/junção(ões) não foram seguidos."));
        }

        SearchSummary = string.Join(" ", parts);
        _mode = CleanupMode.Quick;
        NotifyModes();
        SetSelection(_all);
        Stage = CleanupStage.Review;
        _main.NotifyStatus(string.Create(c, $"Limpeza: {result.Stats.Eligible:N0} temporário(s) antigo(s) elegível(is), {ByteSize.Format(result.Stats.EligibleBytes, c)} — nada foi alterado."));
    }

    // ---- selection ---------------------------------------------------------------------------------------------------

    private void SetMode(CleanupMode mode, bool apply = false)
    {
        bool changed = _mode != mode;
        _mode = mode;
        NotifyModes();
        if (!apply || !changed || !CanEditSelection)
        {
            return;
        }

        switch (mode)
        {
            case CleanupMode.Quick:
                SetSelection(_all);
                GoalMessage = string.Empty;
                break;
            case CleanupMode.Goal:
                ApplyGoal();
                break;
            default:
                GoalMessage = string.Empty;
                break;
        }
    }

    private void NotifyModes()
    {
        OnPropertyChanged(nameof(IsQuick));
        OnPropertyChanged(nameof(IsCustom));
        OnPropertyChanged(nameof(IsGoal));
    }

    private void ApplyGoal()
    {
        if (!TryGetGoalBytes(out long bytes))
        {
            GoalMessage = "Digite um tamanho válido, por exemplo 500 MB ou 2 GB.";
            SetSelection([]);
            return;
        }

        CleanupSelection choice = CleanupPlanner.ChooseForGoal(_all, bytes);
        SetSelection(choice.Items);
        CultureInfo c = CultureInfo.CurrentCulture;
        GoalMessage = choice.GoalReached
            ? string.Create(c, $"Meta atingida na seleção: {ByteSize.Format(choice.TotalBytes, c)} (tamanho lógico), começando pelos mais antigos. Lembre-se: na Lixeira o espaço ainda não é liberado.")
            : string.Create(c, $"Os arquivos que passaram nas verificações somam só {ByteSize.Format(choice.AvailableBytes, c)}; faltam {ByteSize.Format(choice.ShortfallBytes, c)} para a meta. O Tersus não afrouxa as regras para chegar ao número.");
    }

    private bool TryGetGoalBytes(out long bytes)
    {
        bytes = 0;
        string text = (_goalText ?? string.Empty).Trim();
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double v)
            && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
        {
            return false;
        }

        if (!double.IsFinite(v) || v <= 0 || v > 100_000)
        {
            return false;
        }

        double factor = _goalUnit == 1 ? 1024d * 1024 * 1024 : 1024d * 1024;
        bytes = (long)Math.Min(v * factor, long.MaxValue / 2d);
        return true;
    }

    private void SetSelection(IEnumerable<CleanupCandidate> items)
    {
        var chosen = new HashSet<string>(items.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
        _suspend = true;
        try
        {
            foreach (CandidateRow row in Candidates)
            {
                row.SetQuietly(chosen.Contains(row.Candidate.Path));
            }
        }
        finally
        {
            _suspend = false;
        }

        RecomputeSelection();
        InvalidatePlan();
    }

    private void OnRowChanged(CandidateRow row)
    {
        if (_suspend)
        {
            return;
        }

        if (_mode != CleanupMode.Custom)
        {
            _mode = CleanupMode.Custom;
            NotifyModes();
            GoalMessage = string.Empty;
        }

        RecomputeSelection();
        InvalidatePlan();
    }

    private void RecomputeSelection()
    {
        int count = 0;
        long bytes = 0;
        foreach (CandidateRow r in Candidates)
        {
            if (r.IsSelected)
            {
                count++;
                bytes += r.Bytes;
            }
        }

        SelectedCount = count;
        SelectedBytes = bytes;
        OnPropertyChanged(nameof(SelectionText));
    }

    private void ApplySort()
    {
        CandidatesView.SortDescriptions.Clear();
        switch (_sortIndex)
        {
            case 1:
                CandidatesView.SortDescriptions.Add(new SortDescription(nameof(CandidateRow.AgeDays), ListSortDirection.Descending));
                CandidatesView.SortDescriptions.Add(new SortDescription(nameof(CandidateRow.Bytes), ListSortDirection.Descending));
                break;
            case 2:
                CandidatesView.SortDescriptions.Add(new SortDescription(nameof(CandidateRow.Name), ListSortDirection.Ascending));
                break;
            default:
                CandidatesView.SortDescriptions.Add(new SortDescription(nameof(CandidateRow.Bytes), ListSortDirection.Descending));
                break;
        }
    }

    // ---- simulation ----------------------------------------------------------------------------------------------------

    public async Task SimulateAsync()
    {
        if (_stack.Policy is not { } policy || Stage != CleanupStage.Review)
        {
            return;
        }

        CleanupCandidate[] chosen = [.. Candidates.Where(r => r.IsSelected).Select(r => r.Candidate)];
        if (chosen.Length == 0)
        {
            return;
        }

        PlanNote = "Simulando… (nada é alterado)";
        CleanupPlan plan = await Task.Run(() => new CleanupPlanner(policy, _stack.FileSystem).Simulate(chosen));
        _plan = plan;
        BuildPlanSummary(plan);
        Stage = CleanupStage.Simulated;
        OnPropertyChanged(nameof(CanRecycle));
        OnPropertyChanged(nameof(Plan));
    }

    private void BuildPlanSummary(CleanupPlan plan)
    {
        CultureInfo c = CultureInfo.CurrentCulture;
        PlanRefusals.Clear();
        foreach ((RefusalReason reason, int count) in plan.RefusedByReason.OrderByDescending(kv => kv.Value))
        {
            PlanRefusals.Add(new ReasonRow(RefusalText.Describe(reason), count));
        }

        OnPropertyChanged(nameof(HasPlanRefusals));
        if (plan.Approved.Count == 0)
        {
            PlanHeadline = "Simulação concluída: nenhum arquivo passou na revalidação.";
            PlanDetail = "Nada será movido. Veja abaixo por que cada arquivo foi recusado. Nada foi alterado no seu computador.";
            PlanNote = string.Empty;
            return;
        }

        PlanHeadline = string.Create(c, $"Simulação concluída — NADA foi alterado. Se você confirmar, {plan.Approved.Count:N0} arquivo(s) ({ByteSize.Format(plan.ApprovedBytes, c)}, tamanho lógico) serão enviados à Lixeira.");
        PlanDetail = plan.Refused.Count > 0
            ? string.Create(c, $"{plan.Refused.Count:N0} arquivo(s) da seleção foram recusados agora, na revalidação (ver abaixo). Eles não serão tocados.")
            : "Todos os arquivos selecionados continuam elegíveis neste momento.";
        PlanNote = SafetyTexts.RecycleBinNote + " " + SafetyTexts.Revalidation;
    }

    private void InvalidatePlan()
    {
        if (_plan is null && Stage != CleanupStage.Simulated)
        {
            return;
        }

        _plan = null;
        PlanRefusals.Clear();
        OnPropertyChanged(nameof(HasPlanRefusals));
        OnPropertyChanged(nameof(CanRecycle));
        OnPropertyChanged(nameof(Plan));
        if (Stage == CleanupStage.Simulated)
        {
            Stage = CleanupStage.Review;
            PlanNote = "Você mudou a seleção: a simulação anterior foi descartada. Simule de novo antes de confirmar.";
        }
    }

    // ---- execution ----------------------------------------------------------------------------------------------------------

    public async Task RecycleAsync()
    {
        CleanupPlan? plan = _plan;
        if (plan is null || plan.Approved.Count == 0 || Stage != CleanupStage.Simulated || _stack.Policy is not { } policy)
        {
            return;
        }

        // A cleanup without a written trail is not acceptable: prove the log can be written BEFORE asking for confirmation.
        var log = new FileCleanupLog(_stack.Files, _main.Services.Paths.NewCleanupLogPath(DateTime.Now));
        try
        {
            log.Append("# Tersus - registro criado, aguardando a confirmação do usuário");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            PlanNote = "Por segurança nada foi movido: o Tersus não conseguiu gravar o registro da limpeza em " + _main.Services.Paths.LogsDirectory + ". (" + ex.Message + ")";
            return;
        }

        // The ONLY way to a confirmation object: the person ticked the box and pressed the button for THIS plan.
        if (!_main.Services.Dialogs.ConfirmRecycle(plan))
        {
            _stack.Files.TryDeleteOwnFile(log.Path!);
            PlanNote = "Você cancelou: nada foi alterado.";
            return;
        }

        ExecutionConfirmation confirmation = ExecutionConfirmation.For(plan, _main.Services.Clock.UtcNow);
        var executor = new CleanupExecutor(policy, _stack.FileSystem, _stack.Recycler, log);

        Stage = CleanupStage.Executing;
        ProgressIndeterminate = false;
        ProgressMax = Math.Max(1, plan.Items.Count);
        ProgressValue = 0;
        ProgressText = "Preparando a Lixeira…";
        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;
        var progress = new Progress<ExecutionProgress>(p =>
        {
            ProgressValue = p.Done;
            ProgressMax = Math.Max(1, p.Total);
            ProgressText = string.Create(CultureInfo.CurrentCulture, $"{p.Done:N0} de {p.Total:N0}  {(p.CurrentPath is null ? string.Empty : System.IO.Path.GetFileName(p.CurrentPath))}");
        });
        try
        {
            CleanupReport report = await Task.Run(() => executor.Execute(plan, confirmation, progress, token));
            ShowReport(report);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OnExecutionError(Exception ex)
    {
        // Something unexpected happened while files may already have been moved: be honest and force a fresh search.
        ResetToStart();
        _main.ReportError("A limpeza foi interrompida por um erro inesperado", ex, filesMayHaveChanged: true);
    }

    private void ShowReport(CleanupReport report)
    {
        CultureInfo c = CultureInfo.CurrentCulture;
        _report = report;
        LogPath = report.LogPath;
        ReportHeadline = report.MovedCount == 0
            ? "Nenhum arquivo foi movido."
            : string.Create(c, $"{report.MovedCount:N0} arquivo(s) foram enviados à Lixeira do Windows ({ByteSize.Format(report.MovedBytes, c)}, tamanho lógico).");
        ReportDetail = string.Create(c, $"{report.SkippedCount:N0} ignorado(s) e {report.FailedCount:N0} com falha. Duração: {report.Duration:mm\\:ss}.");
        ReportSpace = report.FreeBytesBefore is long before && report.FreeBytesAfter is long after
            ? string.Create(c, $"Espaço livre medido no disco: antes {ByteSize.Format(before, c)}, depois {ByteSize.Format(after, c)}. {SafetyTexts.RecycleBinNote}")
            : SafetyTexts.RecycleBinNote;

        ReportPreflight = string.IsNullOrWhiteSpace(report.PreflightMessage)
            ? string.Empty
            : report.PreflightMessage + " Esse arquivo de teste do Tersus (cerca de 100 bytes) fica na Lixeira e pode ser ignorado.";

        if (report.HasUnprovenDeletion)
        {
            ReportWarning = "ATENÇÃO: um arquivo saiu da pasta sem que a Lixeira confirmasse a chegada dele. Por precaução a operação foi interrompida. Abra a Lixeira do Windows e confira. " + (report.AbortReason ?? string.Empty);
        }
        else if (report.Aborted)
        {
            ReportWarning = report.AbortReason;
        }
        else
        {
            ReportWarning = null;
        }

        Results.Clear();
        foreach (CleanupItemResult item in report.Items.OrderByDescending(i => i.Outcome is CleanupItemOutcome.Failed or CleanupItemOutcome.UnprovenPossiblePermanentDeletion)
            .ThenBy(i => i.Outcome)
            .Take(MaxResultRows))
        {
            Results.Add(new ResultRow(item));
        }

        // The list the person saw is stale now: never allow it to be reused.
        _all.Clear();
        Candidates.Clear();
        _plan = null;
        RecomputeSelection();
        OnPropertyChanged(nameof(CanRecycle));
        OnPropertyChanged(nameof(Report));
        OnPropertyChanged(nameof(Plan));
        Stage = CleanupStage.Done;
        _main.NotifyStatus(ReportHeadline);
    }

    // ---- resets ----------------------------------------------------------------------------------------------------------------

    private void ResetResults()
    {
        _all.Clear();
        Candidates.Clear();
        FoundRefusals.Clear();
        PlanRefusals.Clear();
        Results.Clear();
        _plan = null;
        _report = null;
        ReportPreflight = string.Empty;
        SearchSummary = string.Empty;
        PlanHeadline = string.Empty;
        PlanDetail = string.Empty;
        PlanNote = string.Empty;
        GoalMessage = string.Empty;
        ReportWarning = null;
        LogPath = null;
        RecomputeSelection();
        OnPropertyChanged(nameof(HasPlanRefusals));
        OnPropertyChanged(nameof(CanRecycle));
    }

    private void ResetToStart()
    {
        ResetResults();
        _mode = CleanupMode.Quick;
        NotifyModes();
        Stage = CleanupStage.Start;
    }
}

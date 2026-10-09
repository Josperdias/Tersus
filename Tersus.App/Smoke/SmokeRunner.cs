using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Tersus.App.Controls;
using Tersus.App.Services;
using Tersus.App.ViewModels;
using Tersus.App.Views;
using Tersus.Core.Cleanup;
using Tersus.Core.Storage;

namespace Tersus.App.Smoke;

/// <summary>
/// Automated check of the PUBLISHED program, run by the build pipeline on a clean Windows machine: opens the real window with the real
/// services, visits every page, analyses a folder, runs the cleanup flow against a sandbox folder prepared by the pipeline, checks the
/// safety properties of the confirmation window, takes screenshots and fails on any data-binding error, unhandled exception or failed
/// expectation. It uses the data folder it is given, never the real one, and it cannot confirm a cleanup outside its sandbox.
/// </summary>
internal sealed class SmokeRunner
{
    private sealed record StepResult(string Name, bool Passed, TimeSpan Elapsed, string? Detail);

    private sealed class CheckFailedException(string message) : Exception(message);

    private readonly SmokeOptions _o;
    private readonly App _app;
    private readonly SmokeDialogService _dialogs;
    private readonly AppServices _services;
    private readonly MainViewModel _vm;
    private readonly MainWindow _window;
    private readonly BindingErrorListener _bindingErrors = new();
    private readonly List<StepResult> _steps = [];
    private readonly List<string> _layoutWarnings = [];
    private readonly List<string> _shots = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private System.Threading.Timer? _watchdog;
    private int _shotNumber;

    public SmokeRunner(App app, SmokeOptions options)
    {
        _app = app;
        _o = options;
        System.Diagnostics.PresentationTraceSources.Refresh();
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(_bindingErrors);
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;

        _dialogs = new SmokeDialogService(options.CleanupFolder);
        _services = AppServices.Create(_dialogs, new AppPaths(options.OutDir));
        _vm = new MainViewModel(_services);
        _window = new MainWindow(_vm) { ShowActivated = false };
    }

    private async Task RunAsyncCore()
    {
        _window.Show();
        await Settle(300);

        await Step("Início: janela e unidades", async () =>
        {
            Check(_window.IsVisible, "a janela principal não abriu");
            Check(_vm.Home.Drives.Count > 0, "nenhuma unidade foi listada");
            ExpectView<HomeView>();
            await Shot("inicio", "home");
        });

        await Step("Análise de armazenamento", async () =>
        {
            _vm.Home.SetFolder(_o.ScanFolder);
            await _vm.Home.RunAnalysisAsync();
            Check(_vm.CurrentScan is { Completed: true, FileCount: > 0 }, "a análise não terminou com arquivos encontrados em " + _o.ScanFolder);
            await Settle();
            await Shot("inicio-analisado", "home");
        });

        await Step("Visão geral e mapa de blocos", async () =>
        {
            Go("overview");
            await Settle();
            ExpectView<OverviewView>();
            Check(_vm.Overview.Rows.Count > 0, "a visão geral não tem linhas");
            TreemapControl? map = UiTree.Of<TreemapControl>(_window.Host).FirstOrDefault();
            Check(map is { BlockCount: > 0 }, "o mapa de blocos está vazio");
            Check(_vm.Overview.Explanation is not null, "a explicação da pasta selecionada está vazia");
            await Shot("visao-geral", "overview");

            FolderRow? drill = _vm.Overview.Rows.FirstOrDefault(r => r.CanOpen);
            if (drill is not null)
            {
                int before = _vm.Overview.Breadcrumbs.Count;
                _vm.Overview.OpenCommand.Execute(drill);
                await Settle();
                Check(_vm.Overview.Breadcrumbs.Count == before + 1, "abrir uma pasta não aprofundou o caminho");
                await Shot("visao-geral-subpasta", "overview");
                _vm.Overview.UpCommand.Execute(null);
                await Settle();
                Check(_vm.Overview.Breadcrumbs.Count == before, "subir um nível não voltou ao caminho anterior");
            }
        });

        await Step("Maiores arquivos", async () =>
        {
            Go("files");
            await Settle();
            ExpectView<LargeFilesView>();
            Check(_vm.LargeFiles.View.Cast<object>().Any(), "a lista de maiores arquivos está vazia");
            _vm.LargeFiles.Selected = _vm.LargeFiles.View.Cast<FileRow>().First();
            await Settle();
            Check(_vm.LargeFiles.Explanation is not null, "o arquivo selecionado não foi explicado");
            await Shot("maiores-arquivos", "files");
        });

        await Step("Duplicados (somente relatório)", async () =>
        {
            Go("duplicates");
            await Settle();
            ExpectView<DuplicatesView>();
            _vm.Duplicates.Folder = _o.ScanFolder;
            _vm.Duplicates.MinIndex = 0;
            await _vm.Duplicates.RunAsync();
            await Settle();
            await Shot("duplicados", "duplicates");
        });

        await CleanupSteps();

        await Step("Aplicativos instalados (leitura)", async () =>
        {
            Go("apps");
            await _vm.Apps.LoadAsync();
            await Settle();
            ExpectView<AppsView>();
            await Shot("aplicativos", "apps");
        });

        await Step("Histórico", async () =>
        {
            Go("history");
            _vm.History.Reload();
            await Settle();
            ExpectView<HistoryView>();
            Check(_vm.History.HasData, "a análise concluída não gerou um resumo no histórico");
            await Shot("historico", "history");
        });

        await Step("Recomendações (somente leitura)", async () =>
        {
            Go("advice");
            await _vm.Advisor.LoadAsync();
            await Settle();
            ExpectView<AdvisorView>();
            await Shot("recomendacoes", "advice");
        });

        await Step("Segurança e sobre", async () =>
        {
            Go("about");
            await Settle();
            ExpectView<AboutView>();
            Check(_vm.About.NeverList.Count >= 8, "a lista do que o Tersus nunca limpa está incompleta");
            await Shot("seguranca", "about");
        });

        await Step("Zoom da interface (140%)", async () =>
        {
            _vm.UiScale = 1.4;
            Go("overview");
            await Settle();
            await Shot("zoom-visao-geral", "zoom");
            Go("cleanup");
            await Settle();
            await Shot("zoom-limpeza", "zoom");
            _vm.UiScale = 1.0;
        });

        await Step("Paleta de alto contraste (troca e volta)", async () =>
        {
            _app.UseContrastPalette(true);
            Go("home");
            await Settle();
            await Shot("alto-contraste-inicio", "hc");
            Go("cleanup");
            await Settle();
            await Shot("alto-contraste-limpeza", "hc");
            _app.UseContrastPalette(false);
            Go("home");
            await Settle();
        });
    }

    public async Task<int> RunAsync()
    {
        _watchdog = new System.Threading.Timer(_ =>
        {
            try
            {
                _steps.Add(new StepResult("Tempo limite do teste de fumaça", false, _clock.Elapsed, "o teste passou de 6 minutos e foi interrompido"));
                WriteReport();
            }
            finally
            {
                Environment.Exit(3);
            }
        }, null, TimeSpan.FromMinutes(6), Timeout.InfiniteTimeSpan);

        try
        {
            await RunAsyncCore();
        }
        catch (Exception ex)
        {
            _steps.Add(new StepResult("Falha geral do teste de fumaça", false, _clock.Elapsed, ex.ToString()));
        }

        try
        {
            await Settle();
        }
        catch (Exception ex)
        {
            Trace("Settle final falhou: " + ex);
        }

        bool ok;
        try
        {
            ok = WriteReport();
        }
        catch (Exception ex)
        {
            Trace("WriteReport falhou: " + ex);
            ok = false;
        }

        _watchdog.Dispose();
        return ok ? 0 : 1;
    }

    // ---- cleanup flow ----------------------------------------------------------------------------------------------------------

    private async Task CleanupSteps()
    {
        await Step("Limpeza: tela e regras", async () =>
        {
            Go("cleanup");
            await Settle();
            ExpectView<CleanupView>();
            Check(_vm.Cleanup.IsEnabled, "a limpeza está desativada neste computador: " + _vm.Cleanup.DisabledReason);
            Check(_vm.Cleanup.RulesLine.Contains("14", StringComparison.Ordinal), "o texto das regras não menciona 14 dias");
            await Shot("limpeza-inicio", "cleanup");
        });

        if (_o.CleanupFolder is null)
        {
            return;
        }

        await Step("Limpeza: buscar candidatos na pasta de teste", async () =>
        {
            _services.CleanupStartFolder = _o.CleanupFolder;
            await _vm.Cleanup.SearchAsync();
            await Settle();
            Check(_vm.Cleanup.IsReviewing, "a busca não chegou à tela de revisão");
            if (_o.ExpectEligible is int expected)
            {
                Check(_vm.Cleanup.Candidates.Count == expected, $"esperava {expected} candidato(s) elegível(is), encontrou {_vm.Cleanup.Candidates.Count}");
            }

            Check(_vm.Cleanup.SelectedCount == _vm.Cleanup.Candidates.Count, "a limpeza rápida deveria marcar todos os elegíveis");
            foreach (CandidateRow row in _vm.Cleanup.Candidates)
            {
                Check(PathStartsWith(row.Candidate.Path, _o.CleanupFolder), "candidato fora da pasta de teste: " + row.Candidate.Path);
            }

            await Shot("limpeza-revisao", "cleanup");
        });

        await Step("Limpeza: modo por objetivo e seleção personalizada invalidam a simulação", async () =>
        {
            if (_vm.Cleanup.Candidates.Count == 0)
            {
                return;
            }

            _vm.Cleanup.IsGoal = true;
            _vm.Cleanup.GoalText = "1";
            _vm.Cleanup.GoalUnitIndex = 0;
            await Settle();
            Check(_vm.Cleanup.SelectedCount >= 1, "o modo por objetivo não selecionou nada");
            _vm.Cleanup.IsQuick = true;
            await Settle();
            Check(_vm.Cleanup.SelectedCount == _vm.Cleanup.Candidates.Count, "voltar à limpeza rápida não remarcou todos");
        });

        await Step("Limpeza: simulação não altera nada", async () =>
        {
            string[] before = [.. _vm.Cleanup.Candidates.Select(c => c.Candidate.Path)];
            await _vm.Cleanup.SimulateAsync();
            await Settle();
            Check(_vm.Cleanup.IsSimulated, "a simulação não foi concluída");
            Check(_vm.Cleanup.Plan is not null, "não há plano após simular");
            foreach (string p in before)
            {
                Check(File.Exists(p), "a simulação não pode mexer em arquivos, mas sumiu: " + p);
            }

            await Shot("limpeza-simulacao", "cleanup");

            // Changing the selection after a simulation must throw the plan away.
            if (_vm.Cleanup.Candidates.Count > 0)
            {
                CandidateRow first = _vm.Cleanup.Candidates[0];
                first.IsSelected = !first.IsSelected;
                Check(_vm.Cleanup.Plan is null && !_vm.Cleanup.IsSimulated, "mudar a seleção deveria descartar a simulação");
                first.IsSelected = !first.IsSelected;
                await _vm.Cleanup.SimulateAsync();
                Check(_vm.Cleanup.IsSimulated && _vm.Cleanup.Plan is not null, "não foi possível simular de novo");
            }
        });

        await Step("Janela de confirmação: propriedades de segurança", async () =>
        {
            CleanupPlan plan = _vm.Cleanup.Plan ?? throw new CheckFailedException("sem plano para confirmar");
            var win = new RecycleConfirmWindow(plan) { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 40, Top = 40 };
            try
            {
                win.Show();
                await Settle();
                Check(win.CancelControl.IsDefault && win.CancelControl.IsCancel, "“Cancelar” deve ser o botão padrão (Enter e Esc cancelam)");
                Check(!win.ConfirmControl.IsDefault, "o botão de confirmar não pode ser o padrão");
                Check(win.AcknowledgeBox.IsChecked != true, "a caixa de confirmação deve começar desmarcada");
                Check(!win.ConfirmControl.IsEnabled, "o botão de confirmar deve começar desabilitado");
                Check(!win.Confirmed, "a janela não pode começar confirmada");
                win.AcknowledgeBox.IsChecked = true;
                await Settle();
                Check(win.ConfirmControl.IsEnabled, "marcar a caixa deve habilitar o botão");
                win.AcknowledgeBox.IsChecked = false;
                await Settle();
                Check(!win.ConfirmControl.IsEnabled, "desmarcar a caixa deve desabilitar o botão de novo");
                Check(!win.Confirmed, "a janela não pode estar confirmada sem clique");
                await Shot("limpeza-confirmacao", "confirm", win.Content as FrameworkElement);
            }
            finally
            {
                win.Close();
            }
        });

        await Step("Limpeza: cancelar a confirmação não move nada", async () =>
        {
            CleanupPlan plan = _vm.Cleanup.Plan ?? throw new CheckFailedException("sem plano para confirmar");
            string[] paths = [.. plan.Approved.Select(i => i.Candidate.Path)];
            _dialogs.AllowExecute = false;
            int calls = _dialogs.ConfirmCalls;
            await _vm.Cleanup.RecycleAsync();
            await Settle();
            Check(_dialogs.ConfirmCalls == calls + 1, "a confirmação não foi solicitada");
            Check(_vm.Cleanup.IsSimulated, "após cancelar, a tela deve continuar na simulação");
            foreach (string p in paths)
            {
                Check(File.Exists(p), "cancelar não pode mover nada, mas sumiu: " + p);
            }
        });

        if (!_o.Execute)
        {
            return;
        }

        await Step("Limpeza: confirmar move os arquivos elegíveis para a Lixeira", async () =>
        {
            CleanupPlan plan = _vm.Cleanup.Plan ?? throw new CheckFailedException("sem plano para confirmar");
            string[] paths = [.. plan.Approved.Select(i => i.Candidate.Path)];
            _dialogs.AllowExecute = true;
            await _vm.Cleanup.RecycleAsync();
            await Settle(400);
            Check(_vm.Cleanup.IsDone, "a limpeza não terminou");
            CleanupReport report = _vm.Cleanup.Report ?? throw new CheckFailedException("sem relatório");
            Check(!report.Aborted, "a limpeza foi interrompida: " + report.AbortReason);
            Check(!report.HasUnprovenDeletion, "algum arquivo sumiu sem prova na Lixeira");
            if (_o.ExpectMoved is int expected)
            {
                Check(report.MovedCount == expected, $"esperava {expected} arquivo(s) na Lixeira, foram {report.MovedCount}");
            }

            foreach (CleanupItemResult r in report.Items.Where(i => i.Outcome == CleanupItemOutcome.MovedToRecycleBin))
            {
                Check(!File.Exists(r.Path), "o arquivo deveria ter saído da pasta: " + r.Path);
            }

            Check(paths.Length == report.MovedCount + report.SkippedCount + report.FailedCount, "o relatório não contabiliza todos os arquivos do plano");
            Check(report.LogPath is not null && File.Exists(report.LogPath), "o registro da limpeza não foi gravado");
            await Shot("limpeza-resultado", "cleanup");
        });
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------------------------

    private async Task Step(string name, Func<Task> body)
    {
        var sw = Stopwatch.StartNew();
        Trace("> " + name);
        try
        {
            await body();
            _steps.Add(new StepResult(name, true, sw.Elapsed, null));
            Trace("< ok: " + name);
        }
        catch (Exception ex)
        {
            _steps.Add(new StepResult(name, false, sw.Elapsed, ex is CheckFailedException ? ex.Message : ex.ToString()));
            Trace("< FALHOU: " + name + " :: " + (ex is CheckFailedException ? ex.Message : ex.ToString()));
        }
    }

    /// <summary>Step-by-step trail, flushed as it happens, so a crash still says where it was.</summary>
    private void Trace(string message)
    {
        try
        {
            _services.DataFiles.AppendLine(Path.Combine(_o.OutDir, "smoke-trace.log"), string.Create(CultureInfo.InvariantCulture, $"{_clock.Elapsed.TotalSeconds:F2}s {message}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // The trail is a diagnostic aid only.
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new CheckFailedException(message);
        }
    }

    private void Go(string id) => _vm.Navigate(id);

    private void ExpectView<T>()
        where T : DependencyObject =>
        Check(UiTree.Of<T>(_window.Host).Any(), $"a tela {typeof(T).Name} não foi criada");

    private static bool PathStartsWith(string path, string folder) =>
        path.StartsWith(folder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private async Task Settle(int milliseconds = 150)
    {
        _window.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(milliseconds);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        _window.UpdateLayout();
    }

    private async Task Shot(string name, string page, FrameworkElement? element = null)
    {
        FrameworkElement target = element ?? (FrameworkElement)_window.Content;
        target.UpdateLayout();
        foreach (string clipped in UiTree.Clipped(target).Distinct())
        {
            _layoutWarnings.Add($"[{page}] {clipped}");
        }

        DpiScale dpi = VisualTreeHelper.GetDpi(target);
        int w = Math.Max(1, (int)Math.Ceiling(target.ActualWidth * dpi.DpiScaleX));
        int h = Math.Max(1, (int)Math.Ceiling(target.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(w, h, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(target);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        string file = string.Create(CultureInfo.InvariantCulture, $"{++_shotNumber:00}-{name}.png");
        _services.DataFiles.WriteAllBytesAtomic(Path.Combine(_o.OutDir, "screenshots", file), ms.ToArray());
        _shots.Add(file);
        await Task.CompletedTask;
    }

    private bool WriteReport()
    {
        bool stepsOk = _steps.All(s => s.Passed);
        IReadOnlyList<string> binding = _bindingErrors.Messages;
        bool ok = stepsOk && binding.Count == 0 && App.SmokeUnhandled.Count == 0;

        var sb = new StringBuilder();
        sb.AppendLine("# Tersus — teste de fumaça da interface (programa publicado)");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Resultado: **{(ok ? "APROVADO" : "REPROVADO")}**");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Versão do Tersus: {_vm.VersionText}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Sistema: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}); {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Alto contraste do Windows ativo: {SystemParameters.HighContrast}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Duração: {_clock.Elapsed:mm\\:ss}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Pasta analisada: `{_o.ScanFolder}`");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Pasta de teste da limpeza: `{_o.CleanupFolder ?? "(não usada)"}`; confirmar limpeza permitido: {_o.Execute}");
        sb.AppendLine();
        sb.AppendLine("## Etapas");
        sb.AppendLine();
        sb.AppendLine("| Etapa | Resultado | Tempo | Detalhe |");
        sb.AppendLine("|---|---|---|---|");
        foreach (StepResult s in _steps)
        {
            string detail = (s.Detail ?? string.Empty).Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", " ⏎ ", StringComparison.Ordinal);
            if (detail.Length > 600)
            {
                detail = detail[..600] + "…";
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"| {s.Name} | {(s.Passed ? "ok" : "**FALHOU**")} | {s.Elapsed.TotalSeconds:F1} s | {detail} |");
        }

        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"## Erros de ligação de dados (binding): {binding.Count}");
        foreach (string b in binding.Take(60))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {b.Replace('\n', ' ').Replace('\r', ' ')}");
        }

        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"## Exceções não tratadas na interface: {App.SmokeUnhandled.Count}");
        foreach (string u in App.SmokeUnhandled.Take(10))
        {
            sb.AppendLine("```");
            sb.AppendLine(u);
            sb.AppendLine("```");
        }

        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"## Avisos de layout (elementos que receberam menos espaço do que pediram): {_layoutWarnings.Distinct().Count()}");
        foreach (string w in _layoutWarnings.Distinct().Take(80))
        {
            sb.AppendLine($"- {w}");
        }

        sb.AppendLine();
        sb.AppendLine("## Capturas de tela");
        foreach (string shot in _shots)
        {
            sb.AppendLine($"- screenshots/{shot}");
        }

        if (_dialogs.Messages.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Mensagens que a interface tentou mostrar");
            foreach (string m in _dialogs.Messages)
            {
                sb.AppendLine($"- {m.Replace('\n', ' ')}");
            }
        }

        _services.DataFiles.WriteAllTextAtomic(Path.Combine(_o.OutDir, "smoke-report.md"), sb.ToString());
        return ok;
    }
}

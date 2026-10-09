using System.Windows;
using System.Windows.Threading;
using Tersus.App.Services;
using Tersus.App.Smoke;
using Tersus.App.ViewModels;
using Tersus.Core.Storage;

namespace Tersus.App;

public partial class App : Application
{
    private const string SingleInstanceName = @"Local\Tersus-8d3f1c52-single-instance";
    private const int MaxUnhandledBeforeExit = 5;
    private Mutex? _singleInstance;
    private MainViewModel? _mainViewModel;
    private int _unhandledCount;

    internal static SmokeOptions? Smoke { get; private set; }

    internal static List<string> SmokeUnhandled { get; } = [];

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Smoke = SmokeOptions.Parse(e.Args);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        UseContrastPalette(SystemParameters.HighContrast);
        SystemParameters.StaticPropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SystemParameters.HighContrast))
            {
                _ = Dispatcher.InvokeAsync(() => UseContrastPalette(SystemParameters.HighContrast));
            }
        };

        if (Smoke is { } smoke)
        {
            _ = RunSmokeAsync(smoke);
            return;
        }

        // Only one Tersus at a time: two instances could otherwise run two cleanups side by side.
        _singleInstance = new Mutex(initiallyOwned: true, SingleInstanceName, out bool first);
        if (!first)
        {
            MessageBox.Show("O Tersus já está aberto. Use a janela que já existe.", "Tersus", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        try
        {
            AppServices services = AppServices.Create(new WpfDialogService());
            _mainViewModel = new MainViewModel(services);
            var window = new MainWindow(_mainViewModel);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            RecordCrash(ex);
            MessageBox.Show("O Tersus não conseguiu iniciar.\n\nNada foi alterado nos seus arquivos.\n\nDetalhe técnico: " + ex.Message, "Tersus", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Swaps the colour palette for the high-contrast one (system colours, so Windows' own theme decides the contrast).</summary>
    internal void UseContrastPalette(bool highContrast)
    {
        string uri = highContrast ? "/Tersus;component/Theme/PaletteHighContrast.xaml" : "/Tersus;component/Theme/Palette.xaml";
        Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri(uri, UriKind.Relative) };
    }

    private async Task RunSmokeAsync(SmokeOptions options)
    {
        int code;
        try
        {
            var runner = new SmokeRunner(this, options);
            code = await runner.RunAsync();
        }
        catch (Exception ex)
        {
            code = 2;
            RecordCrash(ex);
        }

        Shutdown(code);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        RecordCrash(e.Exception);
        if (Smoke is not null)
        {
            SmokeUnhandled.Add(e.Exception.ToString());
            return;
        }

        _unhandledCount++;
        bool cleaning = _mainViewModel?.Cleanup.IsExecuting == true;
        string cleaningNote = cleaning
            ? "\n\nA limpeza estava em andamento: abra a Lixeira do Windows e o registro da limpeza (pasta de dados do Tersus) para conferir o que já foi movido."
            : "\n\nO Tersus não apaga nada sozinho: nenhum arquivo foi alterado por esta falha.";
        MessageBox.Show(
            "Ocorreu um erro inesperado e a última ação pode não ter funcionado." + cleaningNote + "\n\nDetalhe técnico: " + e.Exception.Message,
            "Tersus",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        if (_unhandledCount >= MaxUnhandledBeforeExit)
        {
            Shutdown(1);
        }
    }

    private static void RecordCrash(Exception ex)
    {
        try
        {
            var paths = Smoke is { } s ? new AppPaths(s.OutDir) : new AppPaths();
            var files = new AppDataFiles(paths);
            files.AppendLine(
                Path.Combine(paths.LogsDirectory, $"erro-{DateTime.Now:yyyyMMdd}.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}");
        }
        catch (Exception logEx) when (logEx is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Reporting a failure must never cause another one.
        }
    }
}

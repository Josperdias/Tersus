using System.Windows.Input;
using Tersus.App.Services;

namespace Tersus.App.ViewModels;

/// <summary>"Segurança e sobre": what Tersus never does, what it always does, where its own files live. Plain statements, no marketing.</summary>
public sealed class AboutViewModel : ObservableObject
{
    public AboutViewModel(MainViewModel main)
    {
        Main = main;
        OpenDataFolderCommand = new RelayCommand(() => ShellLauncher.OpenFolder(main.Services.Paths.DataDirectory));
        OpenRecycleBinCommand = new RelayCommand(ShellLauncher.OpenRecycleBin);
    }

    public MainViewModel Main { get; }

    public string Version => Main.VersionText;

    public string DataFolder => Main.Services.Paths.DataDirectory;

    public string OnlyAction => SafetyTexts.OnlyAction;

    public string RecycleBinNote => SafetyTexts.RecycleBinNote;

    public string Revalidation => SafetyTexts.Revalidation;

    public string SizesAreLogical => SafetyTexts.SizesAreLogical;

    public string UnsignedNote => SafetyTexts.UnsignedNote;

    public string CleanupState => Main.Services.Cleanup.Policy is { } p
        ? SafetyTexts.RulesLine(p)
        : "A limpeza está DESATIVADA neste computador: " + (Main.Services.Cleanup.Problem ?? "a pasta TEMP não pôde ser confirmada como segura") + " A análise continua funcionando.";

    public IReadOnlyList<string> NeverList => SafetyTexts.NeverList;

    public IReadOnlyList<string> AlwaysList => SafetyTexts.AlwaysList;

    public ICommand OpenDataFolderCommand { get; }

    public ICommand OpenRecycleBinCommand { get; }
}

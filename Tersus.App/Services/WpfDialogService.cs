using System.Windows;
using Microsoft.Win32;
using Tersus.App.Views;
using Tersus.Core.Cleanup;

namespace Tersus.App.Services;

/// <summary>The real dialogs. Every window is modal, owned by the main window, and defaults to the safe answer.</summary>
public sealed class WpfDialogService : IDialogService
{
    private static Window? Owner => Application.Current?.MainWindow is { IsVisible: true } w ? w : null;

    public string? PickFolder(string? initialFolder)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Escolha a pasta que será analisada",
            Multiselect = false,
        };
        if (!string.IsNullOrWhiteSpace(initialFolder) && Directory.Exists(initialFolder))
        {
            dialog.InitialDirectory = initialFolder;
        }

        bool? ok = Owner is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        return ok == true && !string.IsNullOrWhiteSpace(dialog.FolderName) ? dialog.FolderName : null;
    }

    public bool ConfirmRecycle(CleanupPlan plan)
    {
        var window = new RecycleConfirmWindow(plan) { Owner = Owner };
        window.ShowDialog();
        return window.Confirmed;
    }

    public bool Confirm(string title, string message, string confirmText)
    {
        var window = new MessageWindow(title, message, confirmText) { Owner = Owner };
        window.ShowDialog();
        return window.Confirmed;
    }

    public void ShowMessage(string title, string message)
    {
        var window = new MessageWindow(title, message, null) { Owner = Owner };
        window.ShowDialog();
    }
}

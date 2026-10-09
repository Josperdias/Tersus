using Tersus.Core.Cleanup;

namespace Tersus.App.Services;

/// <summary>All modal interaction goes through this interface, so view models never touch windows and the smoke test can answer for the user.</summary>
public interface IDialogService
{
    string? PickFolder(string? initialFolder);

    /// <summary>The explicit confirmation before anything is sent to the Recycle Bin. True ONLY if the user ticked the box and pressed the button.</summary>
    bool ConfirmRecycle(CleanupPlan plan);

    bool Confirm(string title, string message, string confirmText);

    void ShowMessage(string title, string message);
}

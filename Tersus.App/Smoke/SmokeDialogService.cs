using Tersus.App.Services;
using Tersus.Core.Cleanup;
using Tersus.Core.Policy;

namespace Tersus.App.Smoke;

/// <summary>
/// Stand-in for the dialogs during the automated check. It never blocks (a modal window would hang the pipeline) and it is deliberately
/// stingy: it "confirms" a cleanup ONLY when the check was started with --smoke-execute AND every file of the plan is inside the sandbox
/// folder the pipeline created. Anything else is answered with a refusal.
/// </summary>
internal sealed class SmokeDialogService(string? sandboxFolder) : IDialogService
{
    public bool AllowExecute { get; set; }

    public int ConfirmCalls { get; private set; }

    public int ConfirmedCount { get; private set; }

    public List<string> Messages { get; } = [];

    public string? PickFolder(string? initialFolder) => null;

    public bool ConfirmRecycle(CleanupPlan plan)
    {
        ConfirmCalls++;
        if (!AllowExecute || sandboxFolder is null || plan.Approved.Count == 0)
        {
            return false;
        }

        if (!PathGuard.TryNormalize(sandboxFolder, out string sandbox, out _))
        {
            return false;
        }

        foreach (PlanItem item in plan.Approved)
        {
            if (!PathGuard.IsStrictlyUnder(sandbox, item.Candidate.Path))
            {
                return false;
            }
        }

        ConfirmedCount++;
        return true;
    }

    public bool Confirm(string title, string message, string confirmText)
    {
        Messages.Add($"confirmação recusada: {title}");
        return false;
    }

    public void ShowMessage(string title, string message) => Messages.Add($"{title}: {message}");
}

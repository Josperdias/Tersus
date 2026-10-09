using System.Globalization;
using System.Windows;
using Tersus.App.Services;
using Tersus.Core.Cleanup;
using ByteSize = Tersus.Core.SizeText;

namespace Tersus.App.Views;

/// <summary>
/// The explicit confirmation before anything is sent to the Recycle Bin. By design:
/// "Cancel" is the default button (Enter and Esc both cancel), the confirm button stays disabled until the person ticks the box,
/// and the window says plainly that this is the Recycle Bin (not a permanent deletion) and that no space is freed yet.
/// </summary>
public partial class RecycleConfirmWindow : Window
{
    private const int PreviewCount = 12;

    public sealed record PreviewRow(string Path, string Detail);

    public RecycleConfirmWindow(CleanupPlan plan)
    {
        InitializeComponent();

        // Never taller or wider than the screen's work area (small laptops, high Windows scaling): the text scrolls, the buttons stay visible.
        Rect work = SystemParameters.WorkArea;
        MaxHeight = Math.Min(MaxHeight, Math.Max(360, work.Height - 32));
        Width = Math.Min(Width, Math.Max(MinWidth, work.Width - 32));

        CultureInfo c = CultureInfo.CurrentCulture;
        HeadlineText.Text = string.Create(c, $"{plan.Approved.Count:N0} arquivo(s) temporário(s) antigo(s) — {ByteSize.Format(plan.ApprovedBytes, c)} (tamanho lógico) — serão ENVIADOS À LIXEIRA do Windows.");
        FactNotPermanent.Text = "•  Nada é apagado de forma permanente: você pode restaurar os arquivos pela Lixeira.";
        FactNoSpace.Text = "•  " + SafetyTexts.RecycleBinNote;
        FactRevalidate.Text = "•  " + SafetyTexts.Revalidation;
        FactRules.Text = "•  Só entram arquivos .tmp/.temp da pasta TEMP do seu usuário, criados e modificados há 14 dias ou mais, com até 256 MiB.";

        PreviewList.ItemsSource = plan.Approved
            .Take(PreviewCount)
            .Select(i => new PreviewRow(i.Candidate.Path, string.Create(c, $"{ByteSize.Format(i.Candidate.Length, c)} • {i.Candidate.AgeDays} dias")))
            .ToList();
        MoreText.Text = plan.Approved.Count > PreviewCount
            ? string.Create(c, $"… e mais {plan.Approved.Count - PreviewCount:N0} arquivo(s) da mesma simulação.")
            : string.Empty;

        Loaded += (_, _) => CancelButton.Focus();
    }

    /// <summary>True ONLY when the person ticked the box and pressed "Mover para a Lixeira".</summary>
    public bool Confirmed { get; private set; }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (AckBox.IsChecked != true)
        {
            return;
        }

        Confirmed = true;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        DialogResult = false;
    }

    // Exposed for the automated interface self-check (smoke test), which verifies the safety properties of this window.
    internal System.Windows.Controls.CheckBox AcknowledgeBox => AckBox;

    internal System.Windows.Controls.Button ConfirmControl => ConfirmButton;

    internal System.Windows.Controls.Button CancelControl => CancelButton;
}

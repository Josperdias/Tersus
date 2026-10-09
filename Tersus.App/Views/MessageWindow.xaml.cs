using System.Windows;

namespace Tersus.App.Views;

/// <summary>A small message / yes-no window that matches the app. "Cancel" (or "OK" for a plain message) is always the default.</summary>
public partial class MessageWindow : Window
{
    public MessageWindow(string title, string message, string? confirmText)
    {
        InitializeComponent();
        Title = "Tersus — " + title;
        MessageText.Text = message;
        if (confirmText is null)
        {
            CancelButton.Content = "OK";
            CancelButton.Margin = new Thickness(0);
        }
        else
        {
            ConfirmButton.Content = confirmText;
            ConfirmButton.Visibility = Visibility.Visible;
            CancelButton.Margin = new Thickness(0, 0, 10, 0);
        }

        Loaded += (_, _) => CancelButton.Focus();
    }

    public bool Confirmed { get; private set; }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        DialogResult = false;
    }
}

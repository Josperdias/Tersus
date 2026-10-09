using System.Windows.Controls;
using System.Windows.Input;
using Tersus.App.ViewModels;

namespace Tersus.App.Views;

public partial class OverviewView : UserControl
{
    public OverviewView() => InitializeComponent();

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is OverviewViewModel vm && sender is ListBoxItem { DataContext: FolderRow row } && vm.OpenCommand.CanExecute(row))
        {
            vm.OpenCommand.Execute(row);
            e.Handled = true;
        }
    }
}

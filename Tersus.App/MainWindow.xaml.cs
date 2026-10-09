using System.Windows;
using Tersus.App.ViewModels;

namespace Tersus.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    internal System.Windows.Controls.ContentControl Host => PageHost;

    internal FrameworkElement Root => ScaledRoot;
}

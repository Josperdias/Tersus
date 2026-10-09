using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Tersus.App.ViewModels;

namespace Tersus.App.Views;

public partial class CleanupView : UserControl
{
    private CleanupViewModel? _vm;

    public CleanupView()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    // On a short window the card for the next stage (the simulation, then the result) appears below the fold: bring it into view.
    private void Attach()
    {
        Detach();
        _vm = DataContext as CleanupViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelChanged;
        }
    }

    private void Detach()
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm = null;
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        FrameworkElement? target = e.PropertyName switch
        {
            nameof(CleanupViewModel.IsSimulated) when _vm.IsSimulated => PlanCard,
            nameof(CleanupViewModel.IsDone) when _vm.IsDone => ResultCard,
            _ => null,
        };

        if (target is not null)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => target.BringIntoView()));
        }
    }
}

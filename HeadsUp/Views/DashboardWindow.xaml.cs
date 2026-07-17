using HeadsUp.ViewModels;
using System.Windows;

namespace HeadsUp.Views;

public partial class DashboardWindow : Window
{
    private readonly DashboardViewModel _viewModel;

    public DashboardWindow(DashboardViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;
        Loaded += async (_, _) => await _viewModel.LoadAsync();
    }
}

using System.Windows;
using HeadsUp.ViewModels;

namespace HeadsUp.Views;

public partial class SlackAppSetupWindow : Window
{
    public SlackAppSetupWindow(SlackAppSetupViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
    }
}

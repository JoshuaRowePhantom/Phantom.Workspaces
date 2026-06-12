using Avalonia.Controls;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces;

public partial class LoadingWindow : Window
{
    public LoadingWindow(
        LoadingWindowViewModel viewModel)
    {
        InitializeComponent();
        this.DataContext = viewModel;
    }
}

using Avalonia.Controls;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces;

public partial class LoadingWindow : Window
{
    public LoadingWindow()
    {
        InitializeComponent();
    }

    public LoadingWindow(
        LoadingWindowViewModel viewModel)
        : this()
    {
        this.DataContext = viewModel;
    }
}

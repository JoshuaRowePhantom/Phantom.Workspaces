using Avalonia.Controls;
using Avalonia.Interactivity;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces;

public partial class CredentialManagerDialogWindow : Window
{
    public CredentialManagerDialogWindow()
    {
        this.InitializeComponent();
    }

    public CredentialManagerDialogWindow(CredentialManagerDialogViewModel viewModel)
        : this()
    {
        this.DataContext = viewModel;
    }

    private async void OnDeleteSelectedClicked(object? sender, RoutedEventArgs e)
    {
        if (this.DataContext is CredentialManagerDialogViewModel viewModel)
        {
            await viewModel.DeleteSelectedAsync();
        }
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
    {
        this.Close();
    }
}

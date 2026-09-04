using Avalonia.Controls;
using Avalonia.Interactivity;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces;

public partial class SecretUseDialogWindow : Window
{
    public SecretUseDialogWindow()
    {
        this.InitializeComponent();
    }

    public SecretUseDialogWindow(SecretUseDialogViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        this.InitializeComponent();
        this.DataContext = viewModel;
    }

    private void OnYesClicked(object? sender, RoutedEventArgs e)
    {
        if (this.DataContext is SecretUseDialogViewModel vm)
        {
            vm.YesCommand.Execute(null);
        }

        this.Close();
    }

    private void OnNoClicked(object? sender, RoutedEventArgs e)
    {
        if (this.DataContext is SecretUseDialogViewModel vm)
        {
            vm.NoCommand.Execute(null);
        }

        this.Close();
    }
}

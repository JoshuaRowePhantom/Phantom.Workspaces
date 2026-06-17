using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Controls;

public partial class EntityCardControl : UserControl
{
    public EntityCardControl()
    {
        InitializeComponent();
    }

    private void OnEntityCardTapped(object? sender, TappedEventArgs e)
    {
        // Don't trigger if the tap was on a button (shortcut buttons should not double-trigger)
        if (e.Source is Button)
        {
            return;
        }

        // Find the MainWindowViewModel by walking up the visual tree
        var mainWindow = this.FindAncestorOfType<MainWindow>();
        if (mainWindow?.DataContext is MainWindowViewModel mainWindowViewModel && 
            this.DataContext is SubscribedEntityViewModel entityViewModel)
        {
            if (mainWindowViewModel.ActivateEntityClickCommand.CanExecute(entityViewModel))
            {
                mainWindowViewModel.ActivateEntityClickCommand.Execute(entityViewModel);
            }
        }

        e.Handled = true;
    }
}

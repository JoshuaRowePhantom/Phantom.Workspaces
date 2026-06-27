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

    internal void OnEntityCardTapped(object? sender, TappedEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        ActivateCard();
        e.Handled = true;
    }

    internal virtual void ActivateCard()
    {
        var mainWindow = this.FindAncestorOfType<MainWindow>();
        if (mainWindow?.DataContext is MainWindowViewModel mainWindowViewModel &&
            this.DataContext is EntityCardViewModel { Entity: { } entityViewModel })
        {
            if (mainWindowViewModel.ActivateEntityClickCommand.CanExecute(entityViewModel))
            {
                mainWindowViewModel.ActivateEntityClickCommand.Execute(entityViewModel);
            }
        }
    }

    internal void OnInteractiveChildTapped(object? sender, TappedEventArgs e)
    {
        e.Handled = true;
    }
}

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

    // Issue #1177: build this card's field editors lazily on first realization so virtualized
    // trees pay no schema/type-resolution cost for off-screen cards.
    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (this.DataContext as EntityCardViewModel)?.EnsureFieldEditorsBuilt();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (this.VisualRoot is not null)
        {
            (this.DataContext as EntityCardViewModel)?.EnsureFieldEditorsBuilt();
        }
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

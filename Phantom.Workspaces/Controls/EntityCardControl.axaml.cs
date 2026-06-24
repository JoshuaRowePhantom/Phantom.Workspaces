using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia;
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
        // Don't trigger the card's default open behavior when the tap landed on (or inside)
        // an interactive control such as a button, link, text box or toggle. Those controls
        // perform their own action and must not also open the entity (see issues #22 and #26).
        if (IsInteractiveSource(e.Source, this))
        {
            return;
        }

        // Find the MainWindowViewModel by walking up the visual tree
        var mainWindow = this.FindAncestorOfType<MainWindow>();
        if (mainWindow?.DataContext is MainWindowViewModel mainWindowViewModel &&
            this.DataContext is EntityCardViewModel { Entity: { } entityViewModel })
        {
            if (mainWindowViewModel.ActivateEntityClickCommand.CanExecute(entityViewModel))
            {
                mainWindowViewModel.ActivateEntityClickCommand.Execute(entityViewModel);
            }
        }

        e.Handled = true;
    }

    /// <summary>
    /// Determines whether the tapped <paramref name="source"/> is, or is nested within, an
    /// interactive control located between it and the <paramref name="boundary"/> card root.
    /// Walking the visual tree is required because a routed tap reports the deepest hit element
    /// (for example the <see cref="TextBlock"/> inside a link button), not the button itself.
    /// </summary>
    internal static bool IsInteractiveSource(object? source, Visual boundary)
    {
        for (var current = source as Visual;
            current is not null && !ReferenceEquals(current, boundary);
            current = current.GetVisualParent())
        {
            if (current is Button or TextBox or ComboBox or Slider or ListBox or MenuItem)
            {
                return true;
            }
        }

        return false;
    }
}

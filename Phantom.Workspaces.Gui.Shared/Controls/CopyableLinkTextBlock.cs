using System;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Phantom.Workspaces.Gui.Shared.Controls;

/// <summary>A selectable link: a click activates its command, while a drag selects text.</summary>
public class CopyableLinkTextBlock : SafeSelectableTextBlock
{
    public static readonly StyledProperty<ICommand?> CommandProperty =
        AvaloniaProperty.Register<CopyableLinkTextBlock, ICommand?>(nameof(Command));

    private Point? pressPosition;
    private bool dragged;

    public ICommand? Command
    {
        get => this.GetValue(CommandProperty);
        set => this.SetValue(CommandProperty, value);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        this.pressPosition = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            ? e.GetPosition(this)
            : null;
        this.dragged = false;
        base.OnPointerPressed(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (this.pressPosition is { } start)
        {
            var position = e.GetPosition(this);
            if (Math.Abs(position.X - start.X) > 4 || Math.Abs(position.Y - start.Y) > 4)
                this.dragged = true;
        }
        base.OnPointerMoved(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        var activate = this.pressPosition is not null
            && e.InitialPressMouseButton == MouseButton.Left
            && !this.dragged
            && string.IsNullOrEmpty(this.SelectedText)
            && new Rect(this.Bounds.Size).Contains(e.GetPosition(this));
        this.pressPosition = null;
        base.OnPointerReleased(e);

        if (activate && this.Command?.CanExecute(null) == true)
            this.Command.Execute(null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && (e.Key is Key.Enter or Key.Space) && this.Command?.CanExecute(null) == true)
        {
            this.Command.Execute(null);
            e.Handled = true;
        }
    }
}

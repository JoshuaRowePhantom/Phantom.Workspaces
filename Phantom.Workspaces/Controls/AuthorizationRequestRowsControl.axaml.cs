using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Controls;

public partial class AuthorizationRequestRowsControl : UserControl
{
    private AuthorizationRequestRowsViewModel? rows;
    private bool focusedInitialBlocker;
    private bool attached;
    private bool restoringScroll;
    private bool focusInside;
    public bool IsBootstrap { get; set; }

    public AuthorizationRequestRowsControl()
    {
        this.InitializeComponent();
        this.DataContextChanged += (_, _) => this.ObserveRows();
        this.AttachedToVisualTree += (_, _) =>
        {
            this.attached = true;
            this.ObserveRows();
        };
        this.DetachedFromVisualTree += (_, _) =>
        {
            this.attached = false;
            this.restoringScroll = false;
            this.focusInside = false;
            this.UnobserveRows();
        };
        this.LayoutUpdated += (_, _) => this.RestoreScroll();
        this.RowsScroller.PropertyChanged += (_, arguments) =>
        {
            if (arguments.Property == ScrollViewer.OffsetProperty && this.attached &&
                !this.restoringScroll && this.IsEffectivelyVisible)
            {
                this.rows?.Presentation.CaptureScroll(this.RowsScroller.Offset);
            }
        };
        this.PropertyChanged += (_, arguments) =>
        {
            if (arguments.Property == IsKeyboardFocusWithinProperty && !this.IsKeyboardFocusWithin)
            {
                this.focusInside = false;
            }
        };
        this.AddHandler(InputElement.GotFocusEvent, this.OnRowGotFocus);
    }

    private void ObserveRows()
    {
        this.UnobserveRows();
        this.rows = this.DataContext as AuthorizationRequestRowsViewModel;
        if (this.rows is not null && this.attached)
        {
            this.restoringScroll = true;
            this.rows.Changed += this.OnRowsChanged;
            this.OnRowsChanged(this, EventArgs.Empty);
        }
    }

    private void RestoreScroll()
    {
        if (!this.attached || !this.restoringScroll || this.rows is null ||
            this.RowsScroller.Viewport.Height <= 0)
        {
            return;
        }
        this.RowsScroller.Offset = this.rows.Presentation.ScrollOffset;
        this.restoringScroll = false;
    }

    private void OnRowGotFocus(object? sender, FocusChangedEventArgs arguments)
    {
        var entered = !this.focusInside;
        this.focusInside = true;
        if (entered && arguments.NavigationMethod == NavigationMethod.Tab && this.RestoreKeyboardFocus())
        {
            return;
        }

        if (arguments.Source is Control source)
        {
            var target = source.GetVisualAncestors().OfType<Control>().Prepend(source)
                .FirstOrDefault(control => control.Tag is AuthorizationRowFocusTarget);
            if (target?.DataContext is AuthorizationRequestRowViewModel row &&
                target.Tag is AuthorizationRowFocusTarget focusTarget)
            {
                this.rows?.Presentation.CaptureFocus(row.Identifier, focusTarget);
            }
        }
    }

    public bool RestoreKeyboardFocus()
    {
        if (!this.attached || !this.IsEffectivelyVisible || this.rows is null) { return false; }
        var state = this.rows.Presentation;
        var target = this.GetVisualDescendants().OfType<Control>().FirstOrDefault(control =>
            control.Tag is AuthorizationRowFocusTarget focusTarget && focusTarget == state.FocusTarget &&
            control.DataContext is AuthorizationRequestRowViewModel row && row.Identifier == state.FocusedRequestIdentifier &&
            control.IsEffectivelyEnabled && control.Focusable);
        return target?.Focus(NavigationMethod.Tab) == true;
    }

    private void UnobserveRows()
    {
        if (this.rows is not null) { this.rows.Changed -= this.OnRowsChanged; }
        this.rows = null;
    }

    private void OnRowsChanged(object? sender, EventArgs arguments)
    {
        if (!this.IsBootstrap || this.focusedInitialBlocker || this.rows?.Rows.Any(row => row.CanAct) != true) { return; }
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (this.focusedInitialBlocker || this.rows is null) { return; }
            var button = this.GetVisualDescendants().OfType<Button>().FirstOrDefault(value => value.IsEffectivelyEnabled);
            if (button is not null && button.Focus()) { this.focusedInitialBlocker = true; }
        });
    }

    protected override void OnKeyDown(KeyEventArgs arguments)
    {
        if (arguments.Key == Key.Escape && this.IsBootstrap && this.rows?.Rows.Any(row => row.IsBootstrapBlocking) == true)
        {
            arguments.Handled = true;
        }
        base.OnKeyDown(arguments);
    }
}

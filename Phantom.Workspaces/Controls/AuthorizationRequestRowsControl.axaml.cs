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
    public bool IsBootstrap { get; set; }

    public AuthorizationRequestRowsControl()
    {
        this.InitializeComponent();
        this.DataContextChanged += (_, _) => this.ObserveRows();
        this.AttachedToVisualTree += (_, _) => this.ObserveRows();
        this.DetachedFromVisualTree += (_, _) => this.UnobserveRows();
    }

    private void ObserveRows()
    {
        this.UnobserveRows();
        this.rows = this.DataContext as AuthorizationRequestRowsViewModel;
        if (this.rows is not null)
        {
            this.rows.Changed += this.OnRowsChanged;
            this.OnRowsChanged(this, EventArgs.Empty);
        }
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

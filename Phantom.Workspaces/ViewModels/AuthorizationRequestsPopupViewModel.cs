using System;
using System.Threading.Tasks;

namespace Phantom.Workspaces.ViewModels;

public sealed class AuthorizationRequestsPopupViewModel : TransientPopupViewModel
{
    public AuthorizationRequestsPopupViewModel()
    {
        this.HoldDuration = TimeSpan.Zero;
        this.FadeDuration = TimeSpan.Zero;
    }

    private AuthorizationRequestRowsViewModel rows = null!;
    public required AuthorizationRequestRowsViewModel Rows
    {
        get => this.rows;
        init
        {
            this.rows = value;
            value.Changed += this.OnRowsChanged;
        }
    }

    public int ActionableCount => this.Rows.ActionableCount;
    public string KeyLabel => $"Authorizations ({this.ActionableCount})";
    public void ToggleOpen()
    {
        if (this.IsOpen) { this.Dismiss(); return; }
        this.Rows.SetPresentationMode(AuthorizationRowsPresentationMode.Runtime);
        this.IsAutoClosing = false;
        this.IsOpen = true;
    }

    private void OnRowsChanged(object? sender, EventArgs arguments)
    {
        this.RaisePropertyChanged(nameof(this.ActionableCount));
        this.RaisePropertyChanged(nameof(this.KeyLabel));
    }

    public override ValueTask DisposeAsync()
    {
        this.Rows.Changed -= this.OnRowsChanged;
        this.Dismiss();
        return base.DisposeAsync();
    }
}

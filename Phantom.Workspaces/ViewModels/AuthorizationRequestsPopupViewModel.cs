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
    public string KeyLabel => this.ActionableCount == 0 ? "🤫" : $"🤫 {this.ActionableCount}";
    public string AccessibleLabel => this.ActionableCount == 0 ? "Credential access" :
        $"Credential access — {this.ActionableCount} requests need attention";
    public void ShowRequests()
    {
        this.IsAutoClosing = false;
        this.IsOpen = true;
    }
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
        this.RaisePropertyChanged(nameof(this.AccessibleLabel));
    }

    public override ValueTask DisposeAsync()
    {
        this.Rows.Changed -= this.OnRowsChanged;
        this.Dismiss();
        return base.DisposeAsync();
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Phantom.Workspaces.Llm.Secrets;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Services.Secrets;

public sealed class AvaloniaSecretUseDialogHost : ISecretUseDialogHost
{
    private readonly ICredentialPicker credentialPicker;

    public AvaloniaSecretUseDialogHost(ICredentialPicker credentialPicker)
    {
        this.credentialPicker = credentialPicker;
    }

    public async Task<SecretUseDialogResult> ShowAsync(SecretUseDialogInput input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // The Avalonia Window ctor and ShowDialog/Show must run on the UI thread. When called from a
        // background thread (e.g. the MCP OAuth redirect flow) re-enter on the UI thread so all
        // Avalonia object construction and dialog display happen there.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            return await Dispatcher.UIThread.InvokeAsync(() => this.ShowAsync(input, ct));
        }

        var viewModel = new SecretUseDialogViewModel(input, this.credentialPicker);
        var window = new SecretUseDialogWindow(viewModel);
        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is not null)
        {
            await window.ShowDialog(owner).ConfigureAwait(true);
        }
        else
        {
            window.Show();
        }

        return viewModel.DialogResult == true
            ? new SecretUseDialogResult(true, viewModel.SelectedRows)
            : new SecretUseDialogResult(false, []);
    }
}

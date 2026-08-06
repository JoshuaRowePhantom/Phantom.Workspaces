using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
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

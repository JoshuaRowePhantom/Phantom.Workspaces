using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Phantom.Workspaces.Llm.Secrets;
using Phantom.Workspaces.Services.Secrets;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests.Secrets;

public sealed class AvaloniaSecretUseDialogHostTests
{
    [AvaloniaFact(Timeout = 15_000)]
    public async Task AvaloniaSecretUseDialogHost_WhenCalledFromBackgroundThread_MarshalsToUiThread()
    {
        var host = new AvaloniaSecretUseDialogHost(new FakeCredentialPicker());
        var input = Input();

        // Regression guard for #1404: invoking from a non-UI thread previously threw
        // Dispatcher.VerifyAccess when the Window ctor ran off the UI thread.
        var result = await Task.Run(() => host.ShowAsync(input, CancellationToken.None));

        Assert.NotNull(result);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task AvaloniaSecretUseDialogHost_WhenCalledFromUiThread_ShowsDialog()
    {
        Assert.True(Dispatcher.UIThread.CheckAccess());
        var host = new AvaloniaSecretUseDialogHost(new FakeCredentialPicker());

        var result = await host.ShowAsync(Input(), CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.Accepted);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task AvaloniaSecretUseDialogHost_WhenCancelled_ReturnsNotConfirmed()
    {
        var host = new AvaloniaSecretUseDialogHost(new FakeCredentialPicker());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => host.ShowAsync(Input(), cts.Token));
    }

    private static SecretUseDialogInput Input()
    {
        var defaultSource = new CredentialStoreSecretSource("Saved-A");
        var request = new SecretRequest(
            "ApiKey",
            "definition.model.options.additionalProperties.ApiKey",
            [
                new SecretUseMemory(SecretUseScope.AllUses, "All Uses", "h1"),
                new SecretUseMemory(SecretUseScope.KeyInManifestContent, "This Key in This Manifest", "h2"),
                new SecretUseMemory(SecretUseScope.AlwaysAsk, "Always Ask", string.Empty),
            ],
            defaultSource,
            [defaultSource, new AwsLoginSecretSource(), new AzureLoginSecretSource(), new GitHubLoginSecretSource()]);
        return new SecretUseDialogInput([request]);
    }

    private sealed class FakeCredentialPicker : ICredentialPicker
    {
        public bool IsSupported { get; set; } = true;

        public Task<string?> PickAsync(string? initialCredentialName, CancellationToken ct)
            => Task.FromResult<string?>(null);
    }
}

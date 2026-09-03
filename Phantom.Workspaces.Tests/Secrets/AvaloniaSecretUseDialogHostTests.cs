using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Phantom.Workspaces;
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

    [AvaloniaFact(Timeout = 15_000)]
    public void AvaloniaSecretUseDialogHost_ScopeComboBox_ShowsFriendlyLabelNotRecordToString()
    {
        var hash = new string('a', 64);
        var selectedMemory = new SecretUseMemory(SecretUseScope.ManifestIdentity, "This Manifest, Even if Changed", hash);
        var defaultSource = new CredentialStoreSecretSource("Saved-A");
        var request = new SecretRequest(
            "ApiKey",
            "definition.model.options.additionalProperties.ApiKey",
            [selectedMemory, new SecretUseMemory(SecretUseScope.AlwaysAsk, "Always Ask", string.Empty)],
            defaultSource,
            [defaultSource]);
        var vm = new SecretUseDialogViewModel(new SecretUseDialogInput([request]), new FakeCredentialPicker());
        var window = new SecretUseDialogWindow(vm);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var texts = window.GetVisualDescendants().OfType<TextBlock>()
            .Select(textBlock => textBlock.Text)
            .Where(text => text is not null)
            .ToArray();

        Assert.Contains(texts, text => text == selectedMemory.DisplayString);
        Assert.DoesNotContain(texts, text => text == selectedMemory.ToString());
        Assert.DoesNotContain(texts, text => text!.Contains(hash, StringComparison.Ordinal));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void AvaloniaSecretUseDialogHost_SourceComboBox_ShowsFriendlyLabelNotRecordToString()
    {
        var defaultSource = new CredentialStoreSecretSource("Saved-A");
        var request = new SecretRequest(
            "ApiKey",
            "definition.model.options.additionalProperties.ApiKey",
            [new SecretUseMemory(SecretUseScope.AlwaysAsk, "Always Ask", string.Empty)],
            defaultSource,
            [defaultSource]);
        var vm = new SecretUseDialogViewModel(new SecretUseDialogInput([request]), new FakeCredentialPicker());
        var window = new SecretUseDialogWindow(vm);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var texts = window.GetVisualDescendants().OfType<TextBlock>()
            .Select(textBlock => textBlock.Text)
            .Where(text => text is not null)
            .ToArray();

        Assert.Contains(texts, text => text == "Saved credential 'Saved-A'");
        Assert.DoesNotContain(texts, text => text == defaultSource.ToString());
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

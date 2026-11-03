using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Phantom.Workspaces.Controls;
using Phantom.Workspaces.Services.Access;
using Phantom.Workspaces.Tests.Access;

namespace Phantom.Workspaces.Tests;

public sealed class SavedCredentialEditorControlTests
{
    [AvaloniaFact]
    public async Task ViewAndReveal_AreExplicitAndAutomationNeverExposesValue()
    {
        var store = new SavedCredentialAdministrationTests.Store();
        store.Values["Unused"] = "private-marker";
        await using var control = new SavedCredentialsAdministrationControl(new SavedCredentialAdministration(store), new TextBlock());
        var window = new Window { Content = control };
        window.Show();
        try
        {
            await control.RefreshAsync();
            await control.OpenViewAsync(Assert.Single(control.Items));
            Assert.Equal(0, store.Reads);
            Assert.Null(control.ValueField.Text);
            Assert.False(control.ValueField.RevealPassword);
            await control.RevealAsync();
            Assert.Equal(1, store.Reads);
            Assert.Equal("private-marker", control.ValueField.Text);
            Assert.True(control.ValueField.RevealPassword);
            var peer = ControlAutomationPeer.CreatePeerForElement(control.ValueField)!;
            Assert.Equal("Saved credential value", peer.GetName());
            Assert.Equal("password", peer.GetLocalizedControlType());
            Assert.False(peer is IValueProvider);
            Assert.DoesNotContain(peer.GetType().GetInterfaces(), type => type.Name.Contains("TextProvider", StringComparison.Ordinal));
            Assert.Empty(peer.GetChildren());
            Assert.DoesNotContain("private-marker", control.SafeStatus);
            control.Remask();
            Assert.False(control.ValueField.RevealPassword);
            Assert.Null(control.ValueField.Text);
            control.CloseEditor();
            Assert.False(control.IsEditorVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task EditCancelAddAndSave_KeepValuesOnlyInSensitiveControlAndExactStoreEntry()
    {
        var store = new SavedCredentialAdministrationTests.Store();
        store.Values["Existing"] = "original";
        await using var control = new SavedCredentialsAdministrationControl(new SavedCredentialAdministration(store), new TextBlock());
        await control.RefreshAsync();
        await control.OpenEditAsync(Assert.Single(control.Items));
        Assert.False(control.ValueField.RevealPassword);
        Assert.Equal("original", control.ValueField.Text);
        control.ValueField.Text = "discard";
        control.CloseEditor();
        Assert.Equal("original", store.Values["Existing"]);
        Assert.Null(control.ValueField.Text);
        control.OpenAdd();
        control.NameField.Text = "NewName";
        control.ValueField.Text = "new-material";
        await control.SaveAsync();
        Assert.Equal("new-material", store.Values["NewName"]);
        Assert.False(control.IsEditorVisible);
        Assert.Null(control.ValueField.Text);
        Assert.DoesNotContain("new-material", control.SafeStatus);
        await control.RefreshAsync();
        await control.OpenEditAsync(control.Items.Single(row => row.Name == "Existing"));
        control.ValueField.Text = "updated";
        await control.SaveAsync();
        Assert.Equal("updated", store.Values["Existing"]);
        Assert.Equal("new-material", store.Values["NewName"]);
    }

    [AvaloniaFact]
    public async Task FailedSave_RetainsRetryStateAndShowsOnlyAllowlistedError()
    {
        var store = new SavedCredentialAdministrationTests.Store
        {
            BeforeWrite = () => throw new System.IO.IOException("private-material provider payload"),
        };
        await using var control = new SavedCredentialsAdministrationControl(new SavedCredentialAdministration(store), new TextBlock());
        control.OpenAdd();
        control.NameField.Text = "Name";
        control.ValueField.Text = "private-material";
        await control.SaveAsync();
        Assert.True(control.IsEditorVisible);
        Assert.Equal("private-material", control.ValueField.Text);
        Assert.DoesNotContain("private-material", control.SafeStatus);
        Assert.Contains("No success", control.SafeStatus);
        Assert.Empty(store.Values);
        control.CloseEditor();
        Assert.Null(control.ValueField.Text);
    }

    [AvaloniaFact]
    public async Task FocusLossAndWindowDetach_ClearRevealedBuffer_AndDeleteRequiresExactConfirmation()
    {
        var store = new SavedCredentialAdministrationTests.Store();
        store.Values["First"] = "private-marker";
        store.Values["Second"] = "keep";
        await using var control = new SavedCredentialsAdministrationControl(new SavedCredentialAdministration(store), new TextBlock());
        var outside = new Button { Content = "Other control" };
        var window = new Window { Content = new StackPanel { Children = { control, outside } } };
        window.Show();
        try
        {
            await control.RefreshAsync();
            var first = control.Items.Single(row => row.Name == "First");
            await control.OpenViewAsync(first);
            await control.RevealAsync();
            Assert.True(control.ValueField.Focus());
            Assert.True(outside.Focus());
            Assert.False(control.ValueField.RevealPassword);
            Assert.Null(control.ValueField.Text);
            control.OpenDelete(first);
            Assert.Equal(2, store.Values.Count);
            control.CloseEditor();
            Assert.Equal(2, store.Values.Count);
            control.OpenDelete(first);
            await control.DeleteAsync();
            Assert.False(store.Values.ContainsKey("First"));
            Assert.Equal("keep", store.Values["Second"]);
            await control.OpenEditAsync(Assert.Single(control.Items));
            Assert.Equal("keep", control.ValueField.Text);
            window.Content = null;
            Assert.False(control.IsEditorVisible);
            Assert.Null(control.ValueField.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task LateEditorRead_AfterCancel_DoesNotRepopulateOrWrite()
    {
        var store = new DelayedStore();
        await using var control = new SavedCredentialsAdministrationControl(new SavedCredentialAdministration(store), new TextBlock());
        await control.RefreshAsync();
        var read = control.OpenEditAsync(Assert.Single(control.Items));
        await store.Entered.Task;
        control.CloseEditor();
        using var material = SavedCredentialAdministrationTests.Secure("late-private-marker");
        store.Read.SetResult(material.Copy());
        await read;
        Assert.Null(control.ValueField.Text);
        Assert.False(control.IsEditorVisible);
        Assert.Equal(0, store.Writes);
    }

    private sealed class DelayedStore : Llm.Secrets.IPlatformSecretStore
    {
        public bool SupportsNamedCredentialManagement => true;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<System.Security.SecureString?> Read { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Writes { get; private set; }
        public Task<IReadOnlyList<string>> EnumerateNamesAsync(string prefix, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(["Name"]);
        public Task<System.Security.SecureString?> ReadAsync(string name, CancellationToken ct)
        {
            this.Entered.SetResult();
            return this.Read.Task;
        }
        public Task WriteAsync(string name, System.Security.SecureString value, CancellationToken ct)
        { this.Writes++; return Task.CompletedTask; }
        public Task DeleteAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }
}

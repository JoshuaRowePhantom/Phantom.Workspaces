using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Time.Testing;
using Phantom.Workspaces.Controls;
using Phantom.Workspaces.Services.Access;
using Phantom.Workspaces.Tests.Access;

namespace Phantom.Workspaces.Tests;

public sealed class SavedCredentialEditorControlTests
{
    [AvaloniaFact]
    public async Task UnsupportedStore_RendersDisabledNamedActions_IndependentCacheStillUsable()
    {
        var descriptor = new Llm.Interfaces.Access.CredentialCacheDescriptor
        {
            Identifier = new() { Value = Guid.NewGuid() }, Provider = Llm.Interfaces.Access.CredentialProviderKind.Entra,
            Kind = Llm.Interfaces.Access.CredentialKind.AccessToken, Generation = new() { Value = Guid.NewGuid() },
            SourceIdentifier = new() { Digest = new string('a', 64) },
        };
        var administration = new Moq.Mock<Llm.Interfaces.Access.ICredentialCacheAdministration>();
        administration.Setup(service => service.ListAsync(Moq.It.IsAny<CancellationToken>()))
            .Returns(ValueTask.FromResult<IReadOnlyList<Llm.Interfaces.Access.CredentialCacheDescriptor>>([descriptor]));
        await using var model = new ViewModels.CredentialCacheAdministrationViewModel { Administration = administration.Object };
        await model.RefreshAsync(TestContext.Current.CancellationToken);
        var cache = new CredentialCacheAdministrationControl { DataContext = model };
        await using var control = new SavedCredentialsAdministrationControl(
            new SavedCredentialAdministration(new Llm.Secrets.NullPlatformSecretStore()), cache);
        var window = new Window { Content = control };
        window.Show();
        try
        {
            await control.RefreshAsync();
            window.UpdateLayout();
            Assert.Contains(control.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "Named saved-credential management is unavailable on this platform.");
            var add = Assert.Single(control.GetVisualDescendants().OfType<Button>(), button => button.Content as string == "Add credential");
            Assert.False(add.IsEffectivelyEnabled);
            Assert.DoesNotContain(control.GetVisualDescendants().OfType<Button>(),
                button => button.IsEffectivelyVisible && button.Content as string is "View" or "Edit value");
            Assert.False(control.IsEditorVisible);
            Assert.Same(cache, Assert.Single(control.GetVisualDescendants().OfType<CredentialCacheAdministrationControl>()));
            Assert.Same(descriptor, Assert.Single(model.Items));
            Assert.True(model.RefreshCommand.CanExecute(null));
            Assert.True(model.RemoveCommand.CanExecute(descriptor));
            Assert.Contains(cache.GetVisualDescendants().OfType<Button>(), button =>
                button.Content as string == "Delete cached credential" && button.IsEffectivelyEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task PendingReveal_RealFocusLoss_CompletionCannotRestoreRevealOrValue()
    {
        var store = new DelayedStore();
        await using var control = new SavedCredentialsAdministrationControl(new SavedCredentialAdministration(store), new TextBlock());
        var outside = new Button { Content = "Outside" };
        var window = new Window { Content = new StackPanel { Children = { control, outside } } };
        window.Show();
        try
        {
            await control.RefreshAsync();
            await control.OpenViewAsync(Assert.Single(control.Items));
            Assert.True(control.ValueField.Focus());
            Click(control, "Reveal value");
            var pending = Assert.IsAssignableFrom<Task>(control.ActiveOperation);
            await store.Entered.Task;
            Assert.True(outside.Focus());
            using var material = SavedCredentialAdministrationTests.Secure("late-private-marker");
            store.Read.SetResult(material.Copy());
            await pending;
            Assert.Null(control.ValueField.Text);
            Assert.False(control.ValueField.RevealPassword);
            Assert.True(control.IsEditorVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task RevealTimeout_ProductionTimerRoute_RemasksAndClearsWithoutAutomationExposure()
    {
        var clock = new FakeTimeProvider();
        var store = new SavedCredentialAdministrationTests.Store();
        store.Values["Name"] = "private-marker";
        await using var control = new SavedCredentialsAdministrationControl(new SavedCredentialAdministration(store), new TextBlock(), clock);
        var window = new Window { Content = control };
        window.Show();
        try
        {
            await control.RefreshAsync();
            Click(control, "View");
            Click(control, "Reveal value");
            await control.ActiveOperation!;
            Assert.True(control.ValueField.RevealPassword);
            var remasked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            control.ValueField.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBox.RevealPasswordProperty && !control.ValueField.RevealPassword)
                    remasked.TrySetResult();
            };
            clock.Advance(TimeSpan.FromSeconds(30));
            await remasked.Task;
            Assert.False(control.ValueField.RevealPassword);
            Assert.Null(control.ValueField.Text);
            var peer = ControlAutomationPeer.CreatePeerForElement(control.ValueField)!;
            Assert.False(peer is IValueProvider);
            Assert.Empty(peer.GetChildren());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task GuardedButtons_WriteFailureRetainsDirtyEditor_ThenRetryUpdatesExactName()
    {
        var store = new SavedCredentialAdministrationTests.Store();
        store.Values["Name"] = "old";
        store.BeforeWrite = () => throw new System.IO.IOException("private-marker");
        await using var control = new SavedCredentialsAdministrationControl(new SavedCredentialAdministration(store), new TextBlock());
        var window = new Window { Content = control };
        window.Show();
        try
        {
            await control.RefreshAsync();
            Click(control, "Edit value");
            await control.ActiveOperation!;
            Assert.True(control.NameField.IsReadOnly);
            Assert.False(control.ValueField.RevealPassword);
            control.ValueField.Text = "private-marker";
            Click(control, "Save changes");
            await control.ActiveOperation!;
            Assert.Equal("old", store.Values["Name"]);
            Assert.True(control.IsEditorVisible);
            Assert.Equal("private-marker", control.ValueField.Text);
            Assert.DoesNotContain("private-marker", control.SafeStatus);
            Assert.Contains("No success", control.SafeStatus);
            store.BeforeWrite = null;
            Click(control, "Save changes");
            await control.ActiveOperation!;
            Assert.Equal("private-marker", store.Values["Name"]);
            Assert.False(control.IsEditorVisible);
            Assert.Null(control.ValueField.Text);
        }
        finally { window.Close(); }
    }

    private static void Click(Control control, string text)
    {
        var button = Assert.Single(control.GetVisualDescendants().OfType<Button>(),
            candidate => candidate.Content as string == text && candidate.IsEffectivelyVisible);
        Assert.True(button.IsEffectivelyEnabled);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

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

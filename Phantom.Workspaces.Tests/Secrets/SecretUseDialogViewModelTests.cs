using Avalonia.Headless.XUnit;
using Phantom.Workspaces.Llm.Secrets;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests.Secrets;

public sealed class SecretUseDialogViewModelTests
{
    [AvaloniaFact(Timeout = 15_000)]
    public void Ctor_PopulatesRowsFromInput()
    {
        var request = Request("ApiKey");

        var vm = new SecretUseDialogViewModel(new SecretUseDialogInput([request]), new FakeCredentialPicker());

        var row = Assert.Single(vm.Rows);
        Assert.Equal("ApiKey", row.SecretName);
        Assert.Equal("definition.model.options.additionalProperties.ApiKey", row.UseDisplayString);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void Row_MemoryDropdown_DisplaysAllProvidedCandidatesInOrder()
    {
        var request = Request("ApiKey");

        var row = Assert.Single(new SecretUseDialogViewModel(new SecretUseDialogInput([request]), new FakeCredentialPicker()).Rows);

        Assert.Equal(request.Memories, row.AvailableMemories);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void Row_MemoryDropdown_DefaultsToCallerRecommendedMemory()
    {
        var request = Request("ApiKey");

        var row = Assert.Single(new SecretUseDialogViewModel(new SecretUseDialogInput([request]), new FakeCredentialPicker()).Rows);

        Assert.Equal(SecretUseScope.KeyInManifestContent, row.SelectedMemory.Scope);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void Row_SourceDropdown_DisplaysAwsAndAzurePlaceholderEntries()
    {
        var request = Request("ApiKey");

        var row = Assert.Single(new SecretUseDialogViewModel(new SecretUseDialogInput([request]), new FakeCredentialPicker()).Rows);

        Assert.Contains(row.AvailableSources, source => source is AwsLoginSecretSource);
        Assert.Contains(row.AvailableSources, source => source is AzureLoginSecretSource);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void Row_SourceDropdown_DefaultsToDefaultSecretSource()
    {
        var request = Request("ApiKey", defaultSource: new CredentialStoreSecretSource("Saved-A"));

        var row = Assert.Single(new SecretUseDialogViewModel(new SecretUseDialogInput([request]), new FakeCredentialPicker()).Rows);

        Assert.Equal(new CredentialStoreSecretSource("Saved-A"), row.SelectedSource);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void Row_SavedCredentialEllipsisCommand_CanExecute_OnlyWhenCredentialStoreSourceSelected()
    {
        var row = Row(new FakeCredentialPicker());

        Assert.True(row.PickCredentialCommand.CanExecute(null));
        row.SelectedSource = new GitHubLoginSecretSource();
        Assert.False(row.PickCredentialCommand.CanExecute(null));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Row_SavedCredentialEllipsisCommand_InvokesCredentialPicker_WithCurrentCredentialName()
    {
        var picker = new FakeCredentialPicker { Result = "Saved-B" };
        var row = Row(picker);

        row.PickCredentialCommand.Execute(null);
        await row.PickCredentialCommand.LastExecutionTask!;

        Assert.Equal("Saved-A", picker.LastInitialCredentialName);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Row_SavedCredentialEllipsisCommand_PickerReturnsName_ReplacesRowSourceWithCredentialStoreSource()
    {
        var row = Row(new FakeCredentialPicker { Result = "Saved-B" });

        row.PickCredentialCommand.Execute(null);
        await row.PickCredentialCommand.LastExecutionTask!;

        Assert.Equal(new CredentialStoreSecretSource("Saved-B"), row.SelectedSource);
        Assert.Contains(row.AvailableSources, source => source is CredentialStoreSecretSource { CredentialName: "Saved-B" });
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Row_SavedCredentialEllipsisCommand_PickerReturnsNull_LeavesRowUnchanged()
    {
        var row = Row(new FakeCredentialPicker { Result = null });
        var original = row.SelectedSource;

        row.PickCredentialCommand.Execute(null);
        await row.PickCredentialCommand.LastExecutionTask!;

        Assert.Equal(original, row.SelectedSource);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void Row_SavedCredentialEllipsisCommand_PickerIsSupportedFalse_CommandCanExecuteFalse()
    {
        var row = Row(new FakeCredentialPicker { IsSupported = false });

        Assert.False(row.PickCredentialCommand.CanExecute(null));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void YesCommand_SetsDialogResultTrue_WithSelectedRows()
    {
        var vm = new SecretUseDialogViewModel(new SecretUseDialogInput([Request("ApiKey")]), new FakeCredentialPicker());

        vm.YesCommand.Execute(null);

        Assert.True(vm.DialogResult);
        var selected = Assert.Single(vm.SelectedRows);
        Assert.Equal("ApiKey", selected.Request.SecretName);
        Assert.Equal(SecretUseScope.KeyInManifestContent, selected.ChosenMemory.Scope);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public void NoCommand_SetsDialogResultFalse()
    {
        var vm = new SecretUseDialogViewModel(new SecretUseDialogInput([Request("ApiKey")]), new FakeCredentialPicker());

        vm.NoCommand.Execute(null);

        Assert.False(vm.DialogResult);
        Assert.Empty(vm.SelectedRows);
    }

    [Fact]
    public void Rendering_ViewModelDoesNotReferenceManifestOrScopeTypes()
    {
        var members = typeof(SecretUseDialogViewModel).GetMembers()
            .Concat(typeof(SecretUseDialogRowViewModel).GetMembers())
            .Select(member => member.ToString() ?? string.Empty);

        Assert.DoesNotContain(members, member => member.Contains("AgentManifest", StringComparison.Ordinal));
        Assert.DoesNotContain(members, member => member.Contains("SecretUseScope", StringComparison.Ordinal));
    }

    private static SecretUseDialogRowViewModel Row(FakeCredentialPicker picker)
        => Assert.Single(new SecretUseDialogViewModel(new SecretUseDialogInput([Request("ApiKey")]), picker).Rows);

    private static SecretRequest Request(string name, SecretSource? defaultSource = null)
    {
        defaultSource ??= new CredentialStoreSecretSource("Saved-A");
        return new SecretRequest(
            name,
            "definition.model.options.additionalProperties.ApiKey",
            [
                new SecretUseMemory(SecretUseScope.AllUses, "All Uses", "h1"),
                new SecretUseMemory(SecretUseScope.KeyInManifestContent, "This Key in This Manifest", "h2"),
                new SecretUseMemory(SecretUseScope.AlwaysAsk, "Always Ask", string.Empty),
            ],
            defaultSource,
            [defaultSource, new AwsLoginSecretSource(), new AzureLoginSecretSource(), new GitHubLoginSecretSource()]);
    }

    private sealed class FakeCredentialPicker : ICredentialPicker
    {
        public bool IsSupported { get; set; } = true;
        public string? Result { get; set; }
        public string? LastInitialCredentialName { get; private set; }

        public Task<string?> PickAsync(string? initialCredentialName, CancellationToken ct)
        {
            this.LastInitialCredentialName = initialCredentialName;
            return Task.FromResult(this.Result);
        }
    }
}

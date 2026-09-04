using System.Security;
using Phantom.Workspaces.Llm.Secrets;
using Phantom.Workspaces.Services.Secrets;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests.Secrets;

public sealed class CredentialManagerDialogViewModelTests
{
    [Fact]
    public async Task LoadAsync_GroupsMemorizedSecretsBySource()
    {
        var source = new CredentialStoreSecretSource("Prod");
        var allowed = new FakeAllowedSecretsStore(new Dictionary<string, MemorizedSecret>
        {
            ["h1"] = Record("Use A", source),
            ["h2"] = Record("Use B", source),
        });
        var vm = Create(allowed, new FakePlatformSecretStore());

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        var group = Assert.Single(vm.CredentialGroups);
        Assert.Equal("Saved credential 'Prod'", group.DisplayLabel);
        Assert.Equal(["h1", "h2"], group.UsePlaces.Select(static use => use.Hash).Order());
    }

    [Fact]
    public async Task LoadAsync_ListsUnusedSavedCredentials()
    {
        var allowed = new FakeAllowedSecretsStore(new Dictionary<string, MemorizedSecret>
        {
            ["h1"] = Record("Use A", new CredentialStoreSecretSource("Used")),
        });
        var vm = Create(allowed, new FakePlatformSecretStore("Used", "Unused"));

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Unused", Assert.Single(vm.UnusedSavedCredentials).CredentialName);
    }

    [Fact]
    public async Task DeleteSelectedAsync_DeletesMarkedHashesAndUnusedCredentials_ThenReloads()
    {
        var allowed = new FakeAllowedSecretsStore(new Dictionary<string, MemorizedSecret>
        {
            ["h1"] = Record("Use A", new GitHubLoginSecretSource()),
        });
        var platform = new FakePlatformSecretStore("Unused");
        var vm = Create(allowed, platform);
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Single(vm.CredentialGroups).UsePlaces.Single().IsMarkedForDelete = true;
        Assert.Single(vm.UnusedSavedCredentials).IsMarkedForDelete = true;

        await vm.DeleteSelectedAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["h1"], allowed.DeletedHashes);
        Assert.Equal(["Unused"], platform.DeletedNames);
        Assert.Empty(vm.CredentialGroups);
        Assert.Empty(vm.UnusedSavedCredentials);
    }

    [Fact]
    public void SecretSourceDisplay_LabelsSupportedSources()
    {
        Assert.Equal("GitHub login token", SecretSourceDisplay.GetLabel(new GitHubLoginSecretSource()));
        Assert.Equal("Saved credential 'Prod'", SecretSourceDisplay.GetLabel(new CredentialStoreSecretSource("Prod")));
        Assert.Equal("AWS login (not yet implemented)", SecretSourceDisplay.GetLabel(new AwsLoginSecretSource()));
        Assert.Equal("Azure login (not yet implemented)", SecretSourceDisplay.GetLabel(new AzureLoginSecretSource()));
    }

    [Fact]
    public async Task IsSavedCredential_TrueForSavedCredential_FalseForGitHubLogin()
    {
        var allowed = new FakeAllowedSecretsStore(new Dictionary<string, MemorizedSecret>
        {
            ["h1"] = Record("Use A", new CredentialStoreSecretSource("Prod")),
            ["h2"] = Record("Use B", new GitHubLoginSecretSource()),
        });
        var vm = Create(allowed, new FakePlatformSecretStore());

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        var saved = Assert.Single(vm.CredentialGroups, group => group.IsSavedCredential);
        Assert.Equal("Prod", saved.CredentialName);
        Assert.NotNull(saved.EditCommand);

        var gitHub = Assert.Single(vm.CredentialGroups, group => !group.IsSavedCredential);
        Assert.Null(gitHub.CredentialName);
        Assert.Null(gitHub.EditCommand);
    }

    [Fact]
    public async Task EditSavedCredential_InvokesCredentialPicker()
    {
        var source = new CredentialStoreSecretSource("Prod");
        var allowed = new FakeAllowedSecretsStore(new Dictionary<string, MemorizedSecret>
        {
            ["h1"] = Record("Use A", source),
        });
        var picker = new FakeCredentialPicker();
        var vm = Create(allowed, new FakePlatformSecretStore(), picker);
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        var group = Assert.Single(vm.CredentialGroups);
        Assert.NotNull(group.EditCommand);
        group.EditCommand!.Execute(null);
        await group.EditCommand.LastExecutionTask!;

        Assert.Equal("Prod", Assert.Single(picker.PickedNames));
    }

    [Fact]
    public async Task HasSelection_IsFalse_WhenNothingChecked()
    {
        var allowed = new FakeAllowedSecretsStore(new Dictionary<string, MemorizedSecret>
        {
            ["h1"] = Record("Use A", new CredentialStoreSecretSource("Prod")),
        });
        var vm = Create(allowed, new FakePlatformSecretStore("Unused"));

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(vm.HasSelection);
        Assert.False(vm.DeleteSelectedCommand.CanExecute(null));
    }

    [Fact]
    public async Task HasSelection_IsTrue_WhenUsePlaceChecked()
    {
        var allowed = new FakeAllowedSecretsStore(new Dictionary<string, MemorizedSecret>
        {
            ["h1"] = Record("Use A", new CredentialStoreSecretSource("Prod")),
        });
        var vm = Create(allowed, new FakePlatformSecretStore());
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Single(vm.CredentialGroups).UsePlaces.Single().IsMarkedForDelete = true;

        Assert.True(vm.HasSelection);
        Assert.True(vm.DeleteSelectedCommand.CanExecute(null));
    }

    [Fact]
    public async Task HasSelection_IsTrue_WhenUnusedCredentialChecked()
    {
        var allowed = new FakeAllowedSecretsStore(new Dictionary<string, MemorizedSecret>());
        var vm = Create(allowed, new FakePlatformSecretStore("Unused"));
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Single(vm.UnusedSavedCredentials).IsMarkedForDelete = true;

        Assert.True(vm.HasSelection);
        Assert.True(vm.DeleteSelectedCommand.CanExecute(null));
    }

    [Fact]
    public async Task DoesNotExposeSecretValues()
    {
        var allowed = new FakeAllowedSecretsStore(new Dictionary<string, MemorizedSecret>
        {
            ["h1"] = Record("Use A", new CredentialStoreSecretSource("Prod")),
        });
        var platform = new FakePlatformSecretStore("Prod", "Unused");
        var vm = Create(allowed, platform);

        await vm.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Single(vm.CredentialGroups).UsePlaces.Single().IsMarkedForDelete = true;
        Assert.Single(vm.UnusedSavedCredentials).IsMarkedForDelete = true;
        await vm.DeleteSelectedAsync(TestContext.Current.CancellationToken);

        // The manager only enumerates value-free descriptors; it never reads secret material.
        Assert.Empty(platform.ReadNames);
    }

    private static CredentialManagerDialogViewModel Create(FakeAllowedSecretsStore allowed, FakePlatformSecretStore platform)
        => new(allowed, platform, new FakeCredentialPicker(), new SecretMemoryEnumerator(allowed, platform));

    private static CredentialManagerDialogViewModel Create(
        FakeAllowedSecretsStore allowed,
        FakePlatformSecretStore platform,
        FakeCredentialPicker picker)
        => new(allowed, platform, picker, new SecretMemoryEnumerator(allowed, platform));

    private static MemorizedSecret Record(string display, SecretSource source)
        => new(new SecretUseMemory(SecretUseScope.AllUses, display, display), source, DateTimeOffset.UtcNow);

    private sealed class FakeAllowedSecretsStore(IReadOnlyDictionary<string, MemorizedSecret> initial) : IAllowedSecretsStore
    {
        private readonly Dictionary<string, MemorizedSecret> records = new(initial, StringComparer.Ordinal);

        public List<string> DeletedHashes { get; } = [];

        public Task<MemorizedSecret?> TryGetAsync(string hash, CancellationToken ct)
            => Task.FromResult(this.records.TryGetValue(hash, out var record) ? record : null);

        public Task PutAsync(string hash, MemorizedSecret record, CancellationToken ct)
        {
            this.records[hash] = record;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string hash, CancellationToken ct)
        {
            this.DeletedHashes.Add(hash);
            this.records.Remove(hash);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<string, MemorizedSecret>> LoadAllAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<string, MemorizedSecret>>(new Dictionary<string, MemorizedSecret>(this.records));
    }

    private sealed class FakePlatformSecretStore(params string[] names) : IPlatformSecretStore
    {
        private readonly List<string> names = [.. names];

        public List<string> DeletedNames { get; } = [];

        public List<string> ReadNames { get; } = [];

        public Task<SecureString?> ReadAsync(string name, CancellationToken ct)
        {
            this.ReadNames.Add(name);
            return Task.FromResult<SecureString?>(null);
        }

        public Task WriteAsync(string name, SecureString value, CancellationToken ct)
            => Task.CompletedTask;

        public Task DeleteAsync(string name, CancellationToken ct)
        {
            this.DeletedNames.Add(name);
            this.names.Remove(name);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> EnumerateNamesAsync(string prefix, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(this.names.Where(name => name.StartsWith(prefix, StringComparison.Ordinal)).ToArray());
    }

    private sealed class FakeCredentialPicker : ICredentialPicker
    {
        public bool IsSupported => true;

        public List<string?> PickedNames { get; } = [];

        public Task<string?> PickAsync(string? initialCredentialName, CancellationToken ct)
        {
            this.PickedNames.Add(initialCredentialName);
            return Task.FromResult<string?>(initialCredentialName);
        }
    }
}


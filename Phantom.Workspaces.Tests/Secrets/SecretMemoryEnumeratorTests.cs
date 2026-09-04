using System.Security;
using Phantom.Workspaces.Llm.Secrets;
using Phantom.Workspaces.Services.Secrets;

namespace Phantom.Workspaces.Tests.Secrets;

public sealed class SecretMemoryEnumeratorTests
{
    [Fact]
    public async Task SecretMemoryEnumerator_EnumerateAsync_ReturnsGroupsAndUnused()
    {
        var prod = new CredentialStoreSecretSource("Prod");
        var allowed = new FakeAllowedSecretsStore(new Dictionary<string, MemorizedSecret>
        {
            ["h2"] = Record("Use B", prod),
            ["h1"] = Record("Use A", prod),
            ["h3"] = Record("Use C", new GitHubLoginSecretSource()),
        });
        var platform = new FakePlatformSecretStore("Prod", "Unused");
        var enumerator = new SecretMemoryEnumerator(allowed, platform);

        var snapshot = await enumerator.EnumerateAsync(TestContext.Current.CancellationToken);

        // Memorized uses are grouped by source: a saved-credential group (two use places) and the
        // GitHub-login group (one use place).
        var savedGroup = Assert.Single(snapshot.Groups, group => group.Source is CredentialStoreSecretSource);
        Assert.Equal(["Use A", "Use B"], savedGroup.UsePlaces.Select(static use => use.Memory.DisplayString));
        Assert.Equal(["h1", "h2"], savedGroup.UsePlaces.Select(static use => use.Hash));

        var gitHubGroup = Assert.Single(snapshot.Groups, group => group.Source is GitHubLoginSecretSource);
        Assert.Equal("Use C", Assert.Single(gitHubGroup.UsePlaces).Memory.DisplayString);

        // Only saved credentials with no memorized use are reported as unused ("Prod" is used).
        Assert.Equal(["Unused"], snapshot.UnusedSavedCredentialNames);
    }

    private static MemorizedSecret Record(string display, SecretSource source)
        => new(new SecretUseMemory(SecretUseScope.AllUses, display, display), source, DateTimeOffset.UtcNow);

    private sealed class FakeAllowedSecretsStore(IReadOnlyDictionary<string, MemorizedSecret> initial) : IAllowedSecretsStore
    {
        private readonly Dictionary<string, MemorizedSecret> records = new(initial, StringComparer.Ordinal);

        public Task<MemorizedSecret?> TryGetAsync(string hash, CancellationToken ct)
            => Task.FromResult(this.records.TryGetValue(hash, out var record) ? record : null);

        public Task PutAsync(string hash, MemorizedSecret record, CancellationToken ct)
        {
            this.records[hash] = record;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string hash, CancellationToken ct)
        {
            this.records.Remove(hash);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<string, MemorizedSecret>> LoadAllAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<string, MemorizedSecret>>(new Dictionary<string, MemorizedSecret>(this.records));
    }

    private sealed class FakePlatformSecretStore(params string[] names) : IPlatformSecretStore
    {
        private readonly List<string> names = [.. names];

        public Task<SecureString?> ReadAsync(string name, CancellationToken ct)
            => Task.FromResult<SecureString?>(null);

        public Task WriteAsync(string name, SecureString value, CancellationToken ct)
            => Task.CompletedTask;

        public Task DeleteAsync(string name, CancellationToken ct)
        {
            this.names.Remove(name);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> EnumerateNamesAsync(string prefix, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(this.names.Where(name => name.StartsWith(prefix, StringComparison.Ordinal)).ToArray());
    }
}

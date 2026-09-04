using System.Security;
using ModelContextProtocol.Authentication;
using Phantom.Workspaces.Llm.Mcp;
using Phantom.Workspaces.Llm.Secrets;

namespace Phantom.Workspaces.Llm.Core.Tests;

/// <summary>
/// Covers the persistent MCP OAuth token cache (#1384): the <see cref="ITokenCache"/> implementation
/// over <see cref="IPlatformSecretStore"/> (round-trip, per-server keying, overwrite, clear, refresh
/// / expiry preservation) and the DI factory that plugs into the #1382
/// <see cref="McpOAuthOptions.TokenCacheProvider"/> seam. Tests use an in-memory
/// <see cref="IPlatformSecretStore"/> fake and never touch the real Windows Credential Manager.
/// </summary>
public sealed class CredentialManagerTokenCacheTests
{
    private const string ServerName = "github-mcp";

    private static TokenContainer SampleTokens() => new()
    {
        TokenType = "Bearer",
        AccessToken = "access-token-value",
        RefreshToken = "refresh-token-value",
        ExpiresIn = 3600,
        Scope = "repo read:user",
        ObtainedAt = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero),
    };

    [Fact]
    public async Task TokenCache_StoreThenGet_RoundTripsTokenContainer()
    {
        var store = new FakePlatformSecretStore();
        var cache = new CredentialManagerTokenCache(store, ServerName);
        var tokens = SampleTokens();

        await cache.StoreTokensAsync(tokens, CancellationToken.None);
        var loaded = await cache.GetTokensAsync(CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(tokens.TokenType, loaded!.TokenType);
        Assert.Equal(tokens.AccessToken, loaded.AccessToken);
        Assert.Equal(tokens.Scope, loaded.Scope);
    }

    [Fact]
    public async Task TokenCache_GetWithNoStoredTokens_ReturnsNull()
    {
        var cache = new CredentialManagerTokenCache(new FakePlatformSecretStore(), ServerName);

        var loaded = await cache.GetTokensAsync(CancellationToken.None);

        Assert.Null(loaded);
    }

    [Fact]
    public async Task TokenCache_StoresUnderPerServerKey()
    {
        var store = new FakePlatformSecretStore();
        var cache = new CredentialManagerTokenCache(store, ServerName);

        await cache.StoreTokensAsync(SampleTokens(), CancellationToken.None);

        var key = Assert.Single(store.Secrets.Keys);
        Assert.Equal(CredentialManagerTokenCache.KeyPrefix + ServerName, key);
        Assert.Contains(ServerName, key);
    }

    [Fact]
    public async Task TokenCache_DifferentServers_DoNotShareTokens()
    {
        var store = new FakePlatformSecretStore();
        var cacheA = new CredentialManagerTokenCache(store, "server-a");
        var cacheB = new CredentialManagerTokenCache(store, "server-b");

        await cacheA.StoreTokensAsync(SampleTokens(), CancellationToken.None);

        var fromA = await cacheA.GetTokensAsync(CancellationToken.None);
        var fromB = await cacheB.GetTokensAsync(CancellationToken.None);

        Assert.NotNull(fromA);
        Assert.Null(fromB);
    }

    [Fact]
    public async Task TokenCache_OverwriteExistingTokens_ReplacesPreviousValue()
    {
        var store = new FakePlatformSecretStore();
        var cache = new CredentialManagerTokenCache(store, ServerName);

        await cache.StoreTokensAsync(SampleTokens(), CancellationToken.None);
        await cache.StoreTokensAsync(
            new TokenContainer { TokenType = "Bearer", AccessToken = "second-access-token", ObtainedAt = DateTimeOffset.UnixEpoch },
            CancellationToken.None);

        var loaded = await cache.GetTokensAsync(CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("second-access-token", loaded!.AccessToken);
        Assert.Single(store.Secrets);
    }

    [Fact]
    public async Task TokenCache_ClearTokens_RemovesStoredValue()
    {
        var store = new FakePlatformSecretStore();
        var cache = new CredentialManagerTokenCache(store, ServerName);
        await cache.StoreTokensAsync(SampleTokens(), CancellationToken.None);

        await cache.ClearAsync(CancellationToken.None);

        Assert.Empty(store.Secrets);
        Assert.Null(await cache.GetTokensAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TokenCache_SerializesRefreshAndExpiryFields()
    {
        var store = new FakePlatformSecretStore();
        var cache = new CredentialManagerTokenCache(store, ServerName);
        var tokens = SampleTokens();

        await cache.StoreTokensAsync(tokens, CancellationToken.None);
        var loaded = await cache.GetTokensAsync(CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(tokens.RefreshToken, loaded!.RefreshToken);
        Assert.Equal(tokens.ExpiresIn, loaded.ExpiresIn);
        Assert.Equal(tokens.ObtainedAt, loaded.ObtainedAt);
    }

    [Fact]
    public void TokenCacheFactory_OnUnsupportedPlatform_ReturnsNull()
    {
        var nullStoreProvider = CredentialManagerTokenCache.CreateProvider(new NullPlatformSecretStore());
        var noStoreProvider = CredentialManagerTokenCache.CreateProvider(null);

        Assert.Null(nullStoreProvider(ServerName));
        Assert.Null(noStoreProvider(ServerName));
    }

    [Fact]
    public void TokenCacheFactory_IsRegisteredIntoTransportOAuthSeam()
    {
        var store = new FakePlatformSecretStore();
        var options = new McpOAuthOptions
        {
            TokenCacheProvider = CredentialManagerTokenCache.CreateProvider(store),
        };

        var cache = options.ResolveTokenCache(ServerName);

        Assert.NotNull(cache);
        Assert.IsType<CredentialManagerTokenCache>(cache);
    }

    private sealed class FakePlatformSecretStore : IPlatformSecretStore
    {
        public Dictionary<string, SecureString> Secrets { get; } = new(StringComparer.Ordinal);

        public Task<SecureString?> ReadAsync(string name, CancellationToken ct)
            => Task.FromResult(this.Secrets.TryGetValue(name, out var value) ? Copy(value) : null);

        public Task WriteAsync(string name, SecureString value, CancellationToken ct)
        {
            this.Secrets[name] = Copy(value);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string name, CancellationToken ct)
        {
            this.Secrets.Remove(name);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> EnumerateNamesAsync(string prefix, CancellationToken ct)
        {
            IReadOnlyList<string> names = this.Secrets.Keys
                .Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
                .ToArray();
            return Task.FromResult(names);
        }

        private static SecureString Copy(SecureString value)
            => Phantom.Workspaces.Llm.Secrets.SecureStringMarshal.Use(value, ToSecureString);

        private static SecureString ToSecureString(string value)
        {
            var secure = new SecureString();
            foreach (var character in value)
            {
                secure.AppendChar(character);
            }

            secure.MakeReadOnly();
            return secure;
        }
    }
}

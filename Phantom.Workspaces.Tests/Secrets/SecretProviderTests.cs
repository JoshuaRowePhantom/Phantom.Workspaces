using System.Security;
using Phantom.Workspaces.Llm.Secrets;
using Phantom.Workspaces.Services.Secrets;

namespace Phantom.Workspaces.Tests.Secrets;

public sealed class SecretProviderTests
{
    [Fact]
    public async Task RequestSecretsAsync_EmptyRequestList_ReturnsEmptyResult_WithoutShowingDialog()
    {
        var provider = CreateProvider();

        var result = await provider.RequestSecretsAsync([], CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result.AcquiredSecrets);
        Assert.Empty(result.FailedSecrets);
        Assert.Equal(0, provider.Dialog.ShowCount);
        Assert.Equal(0, provider.AllowedStore.LoadAllCount);
    }

    [Fact]
    public async Task RequestSecretsAsync_AllRequestsPreApproved_SkipsDialog()
    {
        var memory = Memory(SecretUseScope.KeyInManifestContent, "hash-approved");
        var source = new CredentialStoreSecretSource("Credential-A");
        var provider = CreateProvider(allowed: new Dictionary<string, MemorizedSecret>
        {
            [memory.Hash] = new(memory, source, DateTimeOffset.UtcNow),
        });
        provider.PlatformStore.StoredSecrets[source.CredentialName] = ToSecureString("credential-value");

        var result = await provider.RequestSecretsAsync([Request("ApiKey", memories: [memory])], CancellationToken.None);

        Assert.NotNull(result);
        Assert.Single(result.AcquiredSecrets);
        Assert.Empty(result.FailedSecrets);
        Assert.Equal(0, provider.Dialog.ShowCount);
    }

    [Fact]
    public async Task RequestSecretsAsync_UnapprovedRequest_ShowsDialog()
    {
        var provider = CreateProvider();
        var request = Request("ApiKey");
        provider.Dialog.Result = Accepted(Row(request, request.Memories[0], request.CandidateSecretSources[0]));
        provider.PlatformStore.StoredSecrets["Credential-A"] = ToSecureString("credential-value");

        var result = await provider.RequestSecretsAsync([request], CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, provider.Dialog.ShowCount);
        Assert.Same(request, Assert.Single(provider.Dialog.LastInput!.Rows));
        Assert.Single(result.AcquiredSecrets);
    }

    [Fact]
    public async Task RequestSecretsAsync_UserClicksNo_ReturnsNull()
    {
        var provider = CreateProvider();
        provider.Dialog.Result = new SecretUseDialogResult(false, []);

        var result = await provider.RequestSecretsAsync([Request("ApiKey")], CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(provider.AllowedStore.Puts);
    }

    [Fact]
    public async Task RequestSecretsAsync_UserClicksYesWithContentScope_PersistsMemorizedSecret()
    {
        var request = Request("ApiKey");
        var chosenMemory = Memory(SecretUseScope.ManifestContent, "hash-content");
        var chosenSource = new CredentialStoreSecretSource("Credential-B");
        var provider = CreateProvider();
        provider.Dialog.Result = Accepted(Row(request, chosenMemory, chosenSource));
        provider.PlatformStore.StoredSecrets[chosenSource.CredentialName] = ToSecureString("credential-value");

        await provider.RequestSecretsAsync([request], CancellationToken.None);

        var put = Assert.Single(provider.AllowedStore.Puts);
        Assert.Equal(chosenMemory.Hash, put.Hash);
        Assert.Equal(chosenMemory, put.Record.Memory);
        Assert.Equal(chosenSource, put.Record.Source);
    }

    [Fact]
    public async Task RequestSecretsAsync_UserClicksYesWithAlwaysAsk_DoesNotPersist()
    {
        var request = Request("ApiKey");
        var provider = CreateProvider();
        provider.Dialog.Result = Accepted(Row(request, Memory(SecretUseScope.AlwaysAsk, string.Empty), new CredentialStoreSecretSource("Credential-A")));
        provider.PlatformStore.StoredSecrets["Credential-A"] = ToSecureString("credential-value");

        await provider.RequestSecretsAsync([request], CancellationToken.None);

        Assert.Empty(provider.AllowedStore.Puts);
    }

    [Fact]
    public async Task RequestSecretsAsync_ManifestContentChanged_PreviousContentScopeConsentDoesNotMatch()
    {
        var previousMemory = Memory(SecretUseScope.ManifestContent, "old-content-hash");
        var request = Request("ApiKey", memories: [Memory(SecretUseScope.ManifestContent, "new-content-hash")]);
        var provider = CreateProvider(allowed: new Dictionary<string, MemorizedSecret>
        {
            [previousMemory.Hash] = new(previousMemory, new CredentialStoreSecretSource("Credential-A"), DateTimeOffset.UtcNow),
        });
        provider.Dialog.Result = Accepted(Row(request, request.Memories[0], new CredentialStoreSecretSource("Credential-A")));
        provider.PlatformStore.StoredSecrets["Credential-A"] = ToSecureString("credential-value");

        await provider.RequestSecretsAsync([request], CancellationToken.None);

        Assert.Equal(1, provider.Dialog.ShowCount);
    }

    [Fact]
    public async Task RequestSecretsAsync_ManifestContentChanged_ManifestIdentityScopeConsentStillMatches()
    {
        var identityMemory = Memory(SecretUseScope.ManifestIdentity, "manifest-id-hash");
        var request = Request("ApiKey", memories: [identityMemory, Memory(SecretUseScope.ManifestContent, "new-content-hash")]);
        var provider = CreateProvider(allowed: new Dictionary<string, MemorizedSecret>
        {
            [identityMemory.Hash] = new(identityMemory, new CredentialStoreSecretSource("Credential-A"), DateTimeOffset.UtcNow),
        });
        provider.PlatformStore.StoredSecrets["Credential-A"] = ToSecureString("credential-value");

        await provider.RequestSecretsAsync([request], CancellationToken.None);

        Assert.Equal(0, provider.Dialog.ShowCount);
    }

    [Fact]
    public async Task RequestSecretsAsync_AnyManifestConsentMatchesAcrossManifests()
    {
        var anyManifest = Memory(SecretUseScope.AnyManifest, "any-manifest-hash");
        var request = Request("ApiKey", memories: [anyManifest, Memory(SecretUseScope.ManifestContent, "different-manifest-content")]);
        var provider = CreateProvider(allowed: new Dictionary<string, MemorizedSecret>
        {
            [anyManifest.Hash] = new(anyManifest, new CredentialStoreSecretSource("Credential-A"), DateTimeOffset.UtcNow),
        });
        provider.PlatformStore.StoredSecrets["Credential-A"] = ToSecureString("credential-value");

        await provider.RequestSecretsAsync([request], CancellationToken.None);

        Assert.Equal(0, provider.Dialog.ShowCount);
    }

    [Fact]
    public async Task RequestSecretsAsync_AllUsesConsentMatchesAcrossManifestsAndAcrossSecretNames()
    {
        var allUses = Memory(SecretUseScope.AllUses, "all-uses-hash");
        var request = Request("DifferentSecretName", memories: [allUses, Memory(SecretUseScope.AnyManifest, "different-secret-hash")]);
        var provider = CreateProvider(allowed: new Dictionary<string, MemorizedSecret>
        {
            [allUses.Hash] = new(allUses, new CredentialStoreSecretSource("Credential-A"), DateTimeOffset.UtcNow),
        });
        provider.PlatformStore.StoredSecrets["Credential-A"] = ToSecureString("credential-value");

        await provider.RequestSecretsAsync([request], CancellationToken.None);

        Assert.Equal(0, provider.Dialog.ShowCount);
    }

    [Fact]
    public async Task RequestSecretsAsync_GitHubLoginSource_DelegatesToGitHubAuthTokenResolver()
    {
        var request = Request("GitHubToken", defaultSource: new GitHubLoginSecretSource(), sources: [new GitHubLoginSecretSource()]);
        var provider = CreateProvider(gitHubTokenResolver: _ => Task.FromResult<string?>("github-token"));
        provider.Dialog.Result = Accepted(Row(request, request.Memories[0], request.CandidateSecretSources[0]));

        var result = await provider.RequestSecretsAsync([request], CancellationToken.None);
        var retriever = Assert.Single(result!.AcquiredSecrets);
        using var secret = await retriever.Secret(CancellationToken.None);

        Assert.Equal(1, provider.GitHubResolverCallCount);
        Assert.Equal("github-token", FromSecureString(secret));
    }

    [Fact]
    public async Task RequestSecretsAsync_GitHubLoginSource_ReturnedRetrieverProducesSecureString()
    {
        var request = Request("GitHubToken", defaultSource: new GitHubLoginSecretSource(), sources: [new GitHubLoginSecretSource()]);
        var provider = CreateProvider(gitHubTokenResolver: _ => Task.FromResult<string?>("github-token"));
        provider.Dialog.Result = Accepted(Row(request, request.Memories[0], request.CandidateSecretSources[0]));

        var result = await provider.RequestSecretsAsync([request], CancellationToken.None);
        var secret = await Assert.Single(result!.AcquiredSecrets).Secret(CancellationToken.None);

        Assert.IsType<SecureString>(secret);
        secret.Dispose();
    }

    [Fact]
    public async Task RequestSecretsAsync_AwsLoginSource_ReturnsNotYetImplementedFailure()
    {
        var result = await RequestFailureForSourceAsync(new AwsLoginSecretSource());

        var failure = Assert.Single(result.FailedSecrets);
        Assert.Equal("AWS login is not yet implemented", failure.FailureReasonDisplayString);
        Assert.Equal(SecretRequestFailureReason.Other, failure.Reason);
    }

    [Fact]
    public async Task RequestSecretsAsync_AzureLoginSource_ReturnsNotYetImplementedFailure()
    {
        var result = await RequestFailureForSourceAsync(new AzureLoginSecretSource());

        var failure = Assert.Single(result.FailedSecrets);
        Assert.Equal("Azure login is not yet implemented", failure.FailureReasonDisplayString);
        Assert.Equal(SecretRequestFailureReason.Other, failure.Reason);
    }

    [Fact]
    public async Task RequestSecretsAsync_CredentialStoreSource_MissingCredential_ReturnsDoesntExistFailure()
    {
        var result = await RequestFailureForSourceAsync(new CredentialStoreSecretSource("MissingCredential"));

        var failure = Assert.Single(result.FailedSecrets);
        Assert.Equal("Credential 'MissingCredential' does not exist", failure.FailureReasonDisplayString);
        Assert.Equal(SecretRequestFailureReason.DoesntExist, failure.Reason);
    }

    [Fact]
    public async Task RequestSecretsAsync_CredentialStoreSource_ReadThrows_ReturnsErrorReadingFailure()
    {
        var request = Request("ApiKey", defaultSource: new CredentialStoreSecretSource("Credential-A"), sources: [new CredentialStoreSecretSource("Credential-A")]);
        var provider = CreateProvider();
        provider.PlatformStore.ReadException = new InvalidOperationException("contains-secret-value");
        provider.Dialog.Result = Accepted(Row(request, request.Memories[0], request.CandidateSecretSources[0]));

        var result = await provider.RequestSecretsAsync([request], CancellationToken.None);

        var failure = Assert.Single(result!.FailedSecrets);
        Assert.Equal("Credential 'Credential-A' could not be read", failure.FailureReasonDisplayString);
        Assert.Equal(SecretRequestFailureReason.ErrorReading, failure.Reason);
        Assert.DoesNotContain("contains-secret-value", failure.FailureReasonDisplayString);
    }

    [Fact]
    public async Task RequestSecretsAsync_LogsAndExceptionsNeverContainSecretValue()
    {
        const string secretValue = "super-secret-token";
        var request = Request("ApiKey", defaultSource: new CredentialStoreSecretSource("Credential-A"), sources: [new CredentialStoreSecretSource("Credential-A")]);
        var provider = CreateProvider();
        provider.PlatformStore.StoredSecrets["Credential-A"] = ToSecureString(secretValue);
        provider.Dialog.Result = Accepted(Row(request, request.Memories[0], request.CandidateSecretSources[0]));

        var result = await provider.RequestSecretsAsync([request], CancellationToken.None);

        Assert.DoesNotContain(result!.FailedSecrets, f => f.FailureReasonDisplayString.Contains(secretValue, StringComparison.Ordinal));
        Assert.DoesNotContain(provider.LogMessages, m => m.Contains(secretValue, StringComparison.Ordinal));
        var secret = await Assert.Single(result.AcquiredSecrets).Secret(CancellationToken.None);
        Assert.DoesNotContain(secretValue, result.ToString(), StringComparison.Ordinal);
        secret.Dispose();
    }

    private static async Task<RequestSecretsResult> RequestFailureForSourceAsync(SecretSource source)
    {
        var request = Request("ApiKey", defaultSource: source, sources: [source]);
        var provider = CreateProvider();
        provider.Dialog.Result = Accepted(Row(request, request.Memories[0], source));

        var result = await provider.RequestSecretsAsync([request], CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result.AcquiredSecrets);
        return result;
    }

    private static TestProvider CreateProvider(
        IReadOnlyDictionary<string, MemorizedSecret>? allowed = null,
        Func<CancellationToken, Task<string?>>? gitHubTokenResolver = null)
    {
        var allowedStore = new FakeAllowedSecretsStore(allowed);
        var platformStore = new FakePlatformSecretStore();
        var dialog = new TestDialogHost();
        var logMessages = new List<string>();
        var callCount = 0;
        Task<string?> Resolver(CancellationToken ct)
        {
            callCount++;
            return gitHubTokenResolver?.Invoke(ct) ?? Task.FromResult<string?>(null);
        }

        var provider = new SecretProvider(allowedStore, platformStore, dialog, Resolver, message => logMessages.Add(message));
        return new TestProvider(provider, allowedStore, platformStore, dialog, logMessages, () => callCount);
    }

    private static SecretRequest Request(
        string secretName,
        IReadOnlyList<SecretUseMemory>? memories = null,
        SecretSource? defaultSource = null,
        IReadOnlyList<SecretSource>? sources = null)
    {
        defaultSource ??= new CredentialStoreSecretSource("Credential-A");
        sources ??= [defaultSource];
        memories ??= [Memory(SecretUseScope.KeyInManifestContent, "hash-" + secretName)];
        return new SecretRequest(secretName, "definition.model.additionalOptions.ApiKey", memories, defaultSource, sources);
    }

    private static SecretUseDialogResult Accepted(params SecretUseDialogRow[] rows)
        => new(true, rows);

    private static SecretUseDialogRow Row(SecretRequest request, SecretUseMemory memory, SecretSource source)
        => new(request, memory, source);

    private static SecretUseMemory Memory(SecretUseScope scope, string hash)
        => new(scope, scope.ToString(), hash);

    private static SecureString ToSecureString(string value)
    {
        var secure = new SecureString();
        foreach (var ch in value)
        {
            secure.AppendChar(ch);
        }

        secure.MakeReadOnly();
        return secure;
    }

    private static string FromSecureString(SecureString value)
        => Phantom.Workspaces.Llm.Secrets.SecureStringMarshal.Use(value, plain => plain);

    private sealed class TestProvider(
        SecretProvider provider,
        FakeAllowedSecretsStore allowedStore,
        FakePlatformSecretStore platformStore,
        TestDialogHost dialog,
        IReadOnlyList<string> logMessages,
        Func<int> gitHubResolverCallCount) : ISecretProvider
    {
        public FakeAllowedSecretsStore AllowedStore { get; } = allowedStore;
        public FakePlatformSecretStore PlatformStore { get; } = platformStore;
        public TestDialogHost Dialog { get; } = dialog;
        public IReadOnlyList<string> LogMessages { get; } = logMessages;
        public int GitHubResolverCallCount => gitHubResolverCallCount();

        public Task<RequestSecretsResult?> RequestSecretsAsync(IReadOnlyList<SecretRequest> requests, CancellationToken cancellationToken)
            => provider.RequestSecretsAsync(requests, cancellationToken);
    }

    private sealed class FakeAllowedSecretsStore(IReadOnlyDictionary<string, MemorizedSecret>? initial = null) : IAllowedSecretsStore
    {
        private readonly Dictionary<string, MemorizedSecret> records = initial?.ToDictionary() ?? [];

        public int LoadAllCount { get; private set; }
        public List<(string Hash, MemorizedSecret Record)> Puts { get; } = [];

        public Task<MemorizedSecret?> TryGetAsync(string hash, CancellationToken ct)
            => Task.FromResult(this.records.TryGetValue(hash, out var record) ? record : null);

        public Task PutAsync(string hash, MemorizedSecret record, CancellationToken ct)
        {
            this.records[hash] = record;
            this.Puts.Add((hash, record));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<string, MemorizedSecret>> LoadAllAsync(CancellationToken ct)
        {
            this.LoadAllCount++;
            return Task.FromResult<IReadOnlyDictionary<string, MemorizedSecret>>(new Dictionary<string, MemorizedSecret>(this.records));
        }
    }

    private sealed class FakePlatformSecretStore : IPlatformSecretStore
    {
        public Dictionary<string, SecureString> StoredSecrets { get; } = [];
        public Exception? ReadException { get; set; }

        public Task<SecureString?> ReadAsync(string name, CancellationToken ct)
        {
            if (this.ReadException is not null)
            {
                throw this.ReadException;
            }

            return Task.FromResult(this.StoredSecrets.GetValueOrDefault(name));
        }

        public Task WriteAsync(string name, SecureString value, CancellationToken ct)
        {
            this.StoredSecrets[name] = value;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string name, CancellationToken ct)
        {
            this.StoredSecrets.Remove(name);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> EnumerateNamesAsync(string prefix, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(this.StoredSecrets.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToList());
    }

    private sealed class TestDialogHost : ISecretUseDialogHost
    {
        public int ShowCount { get; private set; }
        public SecretUseDialogInput? LastInput { get; private set; }
        public SecretUseDialogResult Result { get; set; } = new(true, []);

        public Task<SecretUseDialogResult> ShowAsync(SecretUseDialogInput input, CancellationToken ct)
        {
            this.ShowCount++;
            this.LastInput = input;
            return Task.FromResult(this.Result);
        }
    }
}


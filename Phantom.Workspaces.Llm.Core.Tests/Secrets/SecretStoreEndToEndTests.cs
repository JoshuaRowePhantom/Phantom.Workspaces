using System.Security;
using AgentSchema;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Secrets;
using Phantom.Workspaces.Services.Secrets;

namespace Phantom.Workspaces.Llm.Core.Tests.Secrets;

public sealed class SecretStoreEndToEndTests : IDisposable
{
    private const string SecretName = "GithubApiToken";
    private const string CredentialName = "Phantom.Workspaces:GithubApiToken";
    private const string SecretValue = "gho_test";

    private readonly string directory;
    private readonly string storePath;

    public SecretStoreEndToEndTests()
    {
        this.directory = Path.Combine(Path.GetTempPath(), "secretstore-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.directory);
        this.storePath = Path.Combine(this.directory, "allowed-secrets.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(this.directory))
        {
            Directory.Delete(this.directory, recursive: true);
        }
    }

    [Fact]
    public async Task HappyPath_ContentScope_PersistsConsent_AndSkipsDialogOnSecondCall()
    {
        var platformStore = CreatePlatformStore();
        var firstDialog = DialogChoosing(SecretUseScope.KeyInManifestContent, new CredentialStoreSecretSource(CredentialName));
        var manifest = LoadManifest();

        var first = await MaterializeAndCreateClientAsync(manifest, firstDialog, platformStore);

        Assert.Equal(1, firstDialog.ShowCount);
        Assert.Contains("${SECRET:", first.MaterializedDefinitionJson, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretName, first.MaterializedDefinitionJson, StringComparison.Ordinal);
        Assert.Equal(first.MaterializedDefinitionJson, first.PostClientDefinitionJson);
        AssertClientReceivedApiToken(first.ClientResult, SecretValue);

        var secondDialog = DialogChoosing(SecretUseScope.KeyInManifestContent, new CredentialStoreSecretSource(CredentialName));
        var second = await MaterializeAndCreateClientAsync(manifest, secondDialog, platformStore);

        Assert.Equal(0, secondDialog.ShowCount);
        AssertClientReceivedApiToken(second.ClientResult, SecretValue);
        var allowed = await new AllowedSecretsStore(new AllowedSecretsStoreConfiguration { Path = this.storePath })
            .LoadAllAsync(CancellationToken.None);
        Assert.Single(allowed);
    }

    [Fact]
    public async Task ContentEdit_InvalidatesContentScopeConsent_DialogInvokedAgain()
    {
        var platformStore = CreatePlatformStore();
        var original = LoadManifest(displayName: "Original Display");
        var firstDialog = DialogChoosing(SecretUseScope.KeyInManifestContent, new CredentialStoreSecretSource(CredentialName));
        await MaterializeAndCreateClientAsync(original, firstDialog, platformStore);

        var edited = LoadManifest(displayName: "Edited Display");
        var secondDialog = DialogChoosing(SecretUseScope.KeyInManifestContent, new CredentialStoreSecretSource(CredentialName));
        var second = await MaterializeAndCreateClientAsync(edited, secondDialog, platformStore);

        Assert.Equal(1, secondDialog.ShowCount);
        AssertClientReceivedApiToken(second.ClientResult, SecretValue);
    }

    [Fact]
    public async Task ManifestIdentityConsent_SurvivesContentEdit_DialogSkipped()
    {
        var platformStore = CreatePlatformStore();
        var original = LoadManifest(displayName: "Original Display");
        var identityMemory = new AgentManifestSecretUseMemoryFactory()
            .Build(original, SecretName, "definition.model.options.additionalProperties.ApiToken")
            .Single(memory => memory.Scope == SecretUseScope.ManifestIdentity);
        var allowedStore = new AllowedSecretsStore(new AllowedSecretsStoreConfiguration { Path = this.storePath });
        await allowedStore.PutAsync(
            identityMemory.Hash,
            new MemorizedSecret(identityMemory, new CredentialStoreSecretSource(CredentialName), DateTimeOffset.UtcNow),
            CancellationToken.None);

        var edited = LoadManifest(displayName: "Edited Display");
        var dialog = DialogChoosing(SecretUseScope.KeyInManifestContent, new CredentialStoreSecretSource(CredentialName));
        var result = await MaterializeAndCreateClientAsync(edited, dialog, platformStore);

        Assert.Equal(0, dialog.ShowCount);
        AssertClientReceivedApiToken(result.ClientResult, SecretValue);
    }

    [Fact]
    public async Task AwsPlaceholderSelected_ThrowsSecretMaterializationFailedException_WithNotYetImplementedMessage()
    {
        var platformStore = CreatePlatformStore();
        var dialog = DialogChoosing(SecretUseScope.KeyInManifestContent, new AwsLoginSecretSource());

        var exception = await Assert.ThrowsAsync<SecretMaterializationFailedException>(() =>
            MaterializeAndCreateClientAsync(LoadManifest(), dialog, platformStore));

        var failure = Assert.Single(exception.Failures);
        Assert.Equal(SecretName, failure.SecretName);
        Assert.Equal("AWS login is not yet implemented", failure.FailureReasonDisplayString);
        Assert.Equal(SecretRequestFailureReason.Other, failure.Reason);
    }

    private async Task<EndToEndResult> MaterializeAndCreateClientAsync(
        AgentManifest manifest,
        ScriptedDialogHost dialog,
        FakePlatformSecretStore platformStore)
    {
        var allowedStore = new AllowedSecretsStore(new AllowedSecretsStoreConfiguration { Path = this.storePath });
        var secretProvider = new SecretProvider(allowedStore, platformStore, dialog);
        var definition = await AgentFactory.CreateAgentDefinitionAsync(
            new CreateAgentDefinitionRequest { AgentManifest = manifest },
            CancellationToken.None);

        var materialized = await new AgentDefinitionSecretMaterializer(platformSecretStore: platformStore)
            .MaterializeAsync(manifest, definition, secretProvider, CancellationToken.None);
        var materializedDefinitionJson = materialized.Definition.ToJson();

        var clientResult = await AgentFactory.CreateChatClientAsync(
            materialized.Definition,
            new AgentServices { SecretPlaceholderResolver = materialized.Resolver },
            cancellationToken: CancellationToken.None);

        return new EndToEndResult(clientResult, materializedDefinitionJson, materialized.Definition.ToJson());
    }

    private static AgentManifest LoadManifest(string displayName = "Secret E2E Agent")
        => AgentManifestLoader.LoadManifestFromJson($$"""
        {
          "name": "secret-e2e-agent",
          "displayName": "Secret E2E Manifest",
          "metadata": { "entity-id": "11111111-1111-1111-1111-111111111111" },
          "template": {
            "kind": "prompt",
            "name": "secret-e2e-agent",
            "displayName": "{{displayName}}",
            "model": {
              "id": "gpt-5-mini",
              "provider": "github-copilot",
              "options": {
                "additionalProperties": {
                  "ApiToken": "${SECRET:GithubApiToken}"
                }
              }
            }
          }
        }
        """);

    private static FakePlatformSecretStore CreatePlatformStore()
    {
        var store = new FakePlatformSecretStore();
        store.Secrets[CredentialName] = ToSecureString(SecretValue);
        return store;
    }

    private static ScriptedDialogHost DialogChoosing(SecretUseScope scope, SecretSource source)
        => new(input =>
        {
            var request = Assert.Single(input.Rows);
            var memory = request.Memories.Single(m => m.Scope == scope);
            return new SecretUseDialogResult(true, [new SecretUseDialogRow(request, memory, source)]);
        });

    private static void AssertClientReceivedApiToken(ChatClientResult result, string expected)
    {
        var client = Assert.IsType<CopilotSdkChatClient>(result.ChatClient);
        Assert.NotNull(client.ModelOptions);
        Assert.NotNull(client.ModelOptions!.AdditionalProperties);
        Assert.Equal(expected, client.ModelOptions.AdditionalProperties["ApiToken"]);
    }

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

    private sealed record EndToEndResult(
        ChatClientResult ClientResult,
        string MaterializedDefinitionJson,
        string PostClientDefinitionJson);

    private sealed class ScriptedDialogHost(Func<SecretUseDialogInput, SecretUseDialogResult> script) : ISecretUseDialogHost
    {
        public int ShowCount { get; private set; }

        public Task<SecretUseDialogResult> ShowAsync(SecretUseDialogInput input, CancellationToken ct)
        {
            this.ShowCount++;
            return Task.FromResult(script(input));
        }
    }

    private sealed class FakePlatformSecretStore : IPlatformSecretStore
    {
        public Dictionary<string, SecureString> Secrets { get; } = new(StringComparer.Ordinal);

        public Task<SecureString?> ReadAsync(string name, CancellationToken ct)
        {
            return Task.FromResult(this.Secrets.TryGetValue(name, out var value) ? Copy(value) : null);
        }

        public Task WriteAsync(string name, SecureString value, CancellationToken ct)
        {
            this.Secrets[name] = value;
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
    }
}

using System.Linq;
using AgentSchema;
using Phantom.Workspaces.Llm.Secrets;

namespace Phantom.Workspaces.Llm.Core.Tests.Secrets;

public sealed class AgentManifestSecretUseMemoryFactoryTests
{
    private const string EntityId = "11111111-1111-1111-1111-111111111111";

    private static AgentManifest Manifest(string? entityId, string modelId = "echo") => AgentManifestLoader.LoadManifestFromJson($$"""
    {
      "name": "m",
      "displayName": "M",
      {{(entityId is null ? "" : $"\"metadata\": {{ \"entity-id\": \"{entityId}\" }},")}}
      "template": {
        "kind": "prompt",
        "name": "m",
        "model": { "id": "{{modelId}}", "provider": "echo", "apiType": "Echo" }
      }
    }
    """);

    [Fact]
    public void Build_ReturnsCandidatesOrderedBroadestToNarrowest_EndingInAlwaysAsk()
    {
        var memories = new AgentManifestSecretUseMemoryFactory()
            .Build(Manifest(EntityId), "MySecret", "definition.model.options.additionalProperties.ApiToken");

        var scopes = memories.Select(m => m.Scope).ToArray();
        Assert.Equal(
            new[]
            {
                SecretUseScope.AllUses,
                SecretUseScope.AnyManifest,
                SecretUseScope.KeyInAnyManifest,
                SecretUseScope.ManifestIdentity,
                SecretUseScope.ManifestContent,
                SecretUseScope.KeyInManifestContent,
                SecretUseScope.AlwaysAsk,
            },
            scopes);
        Assert.Equal(SecretUseScope.AlwaysAsk, memories[^1].Scope);
    }

    [Fact]
    public void Build_ManifestWithoutEntityId_OmitsManifestIdentityCandidate()
    {
        var memories = new AgentManifestSecretUseMemoryFactory()
            .Build(Manifest(entityId: null), "MySecret", "use");

        Assert.DoesNotContain(memories, m => m.Scope == SecretUseScope.ManifestIdentity);
        Assert.Equal(6, memories.Count);
    }

    [Fact]
    public void Build_ManifestWithEntityId_IncludesManifestIdentityCandidate()
    {
        var memories = new AgentManifestSecretUseMemoryFactory()
            .Build(Manifest(EntityId), "MySecret", "use");

        Assert.Contains(memories, m => m.Scope == SecretUseScope.ManifestIdentity);
        Assert.Equal(7, memories.Count);
    }

    [Fact]
    public void Build_TwoManifestsSameSecret_KeyInManifestContentHashesDiffer()
    {
        var factory = new AgentManifestSecretUseMemoryFactory();
        var a = factory.Build(Manifest(EntityId, modelId: "model-a"), "MySecret", "use");
        var b = factory.Build(Manifest(EntityId, modelId: "model-b"), "MySecret", "use");

        var hashA = a.Single(m => m.Scope == SecretUseScope.KeyInManifestContent).Hash;
        var hashB = b.Single(m => m.Scope == SecretUseScope.KeyInManifestContent).Hash;

        Assert.NotEqual(hashA, hashB);
    }

    [Fact]
    public void Build_SameManifestEditedAnywhere_ContentScopeHashesChange()
    {
        var factory = new AgentManifestSecretUseMemoryFactory();
        var original = factory.Build(Manifest(EntityId, modelId: "model-a"), "MySecret", "use");
        var edited = factory.Build(Manifest(EntityId, modelId: "model-b"), "MySecret", "use");

        Assert.NotEqual(
            original.Single(m => m.Scope == SecretUseScope.ManifestContent).Hash,
            edited.Single(m => m.Scope == SecretUseScope.ManifestContent).Hash);
        Assert.NotEqual(
            original.Single(m => m.Scope == SecretUseScope.KeyInManifestContent).Hash,
            edited.Single(m => m.Scope == SecretUseScope.KeyInManifestContent).Hash);
    }

    [Fact]
    public void Build_SameManifestEditedAnywhere_ManifestIdentityScopeHashUnchanged()
    {
        var factory = new AgentManifestSecretUseMemoryFactory();
        var original = factory.Build(Manifest(EntityId, modelId: "model-a"), "MySecret", "use");
        var edited = factory.Build(Manifest(EntityId, modelId: "model-b"), "MySecret", "use");

        Assert.Equal(
            original.Single(m => m.Scope == SecretUseScope.ManifestIdentity).Hash,
            edited.Single(m => m.Scope == SecretUseScope.ManifestIdentity).Hash);
    }

    [Fact]
    public void Build_AllUsesCandidate_HashIndependentOfSecretName()
    {
        var factory = new AgentManifestSecretUseMemoryFactory();
        var a = factory.Build(Manifest(EntityId), "SecretA", "use");
        var b = factory.Build(Manifest(EntityId), "SecretB", "use");

        Assert.Equal(
            a.Single(m => m.Scope == SecretUseScope.AllUses).Hash,
            b.Single(m => m.Scope == SecretUseScope.AllUses).Hash);
    }

    [Fact]
    public void Build_AlwaysAskCandidate_HashIsEmpty()
    {
        var memories = new AgentManifestSecretUseMemoryFactory()
            .Build(Manifest(EntityId), "MySecret", "use");

        Assert.Equal(string.Empty, memories.Single(m => m.Scope == SecretUseScope.AlwaysAsk).Hash);
    }

    [Fact]
    public void Build_MemoriesNeverEmbedSecretValues()
    {
        const string secretName = "MyVerySecretName";
        var memories = new AgentManifestSecretUseMemoryFactory()
            .Build(Manifest(EntityId), secretName, "use");

        // The secret name is hashed, never stored verbatim in either the hash or the display string.
        foreach (var memory in memories)
        {
            Assert.DoesNotContain(secretName, memory.Hash);
            Assert.DoesNotContain(secretName, memory.DisplayString);
        }
    }
}

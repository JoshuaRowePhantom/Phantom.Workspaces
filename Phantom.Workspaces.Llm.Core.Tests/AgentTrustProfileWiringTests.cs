using AgentSchema;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Processes;
using Phantom.Workspaces.Llm.Trust;
using Xunit;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class AgentTrustProfileWiringTests
{
    private const string LocalOnlyProfileName = "local-only";
    private const string RemoteOnlyProfileName = "remote-only";

    private static DictionaryTrustProfileProvider CreateProvider()
    {
        var entities = new Dictionary<string, TrustProfileEntity>(StringComparer.Ordinal)
        {
            [LocalOnlyProfileName] = new TrustProfileEntity
            {
                Name = LocalOnlyProfileName,
                Definition = new TrustProfileDefinition { HostingWorkspacesClientInstances = ["."] },
            },
            [RemoteOnlyProfileName] = new TrustProfileEntity
            {
                Name = RemoteOnlyProfileName,
                Definition = new TrustProfileDefinition { HostingWorkspacesClientInstances = ["remote-a"] },
            },
        };

        return new DictionaryTrustProfileProvider(entities);
    }

    private static AgentDefinition CreateEchoAgent(string? trustProfileName)
    {
        var trustMetadata = trustProfileName is null
            ? string.Empty
            : $$"""
                ,
                  "metadata": { "trust-profile": "{{trustProfileName}}" }
                """;

        return AgentDefinitionLoader.LoadAgentFromJson(
            $$"""
            {
              "kind": "prompt",
              "name": "echo-agent",
              "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
              "tools": []{{trustMetadata}}
            }
            """);
    }

    [Fact]
    public async Task Resolve_NoTrustProfileMetadata_ReturnsNull()
    {
        var agent = CreateEchoAgent(trustProfileName: null);

        var resolved = await AgentTrustProfileResolver.ResolveAsync(agent, CreateProvider());

        Assert.Null(resolved);
    }

    [Fact]
    public async Task Resolve_ReferencedProfile_ResolvesComposedProfile()
    {
        var agent = CreateEchoAgent(LocalOnlyProfileName);

        var resolved = await AgentTrustProfileResolver.ResolveAsync(agent, CreateProvider());

        Assert.NotNull(resolved);
        Assert.True(resolved!.AllowsLocalExecution());
    }

    [Fact]
    public async Task CreateAgentChat_LocalPermittedProfile_Succeeds()
    {
        var agent = CreateEchoAgent(LocalOnlyProfileName);

        await using var chat = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = agent,
            TrustProfileProvider = CreateProvider(),
        });

        Assert.NotNull(chat);
    }

    [Fact]
    public async Task CreateAgentChat_RemoteOnlyProfile_ThrowsForLocalExecution()
    {
        var agent = CreateEchoAgent(RemoteOnlyProfileName);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
            {
                AgentDefinition = agent,
                TrustProfileProvider = CreateProvider(),
            }));

        Assert.Contains("does not permit local execution", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAgentChat_StdioMcp_ThreadsCompiledTrustPolicyToExecutor()
    {
        var executablePath = TestMcpServerProcess.GetMcpExecutablePath();
        var endpoint = $"stdio://?command={Uri.EscapeDataString(executablePath)}&arg=--mode&arg=stdio";
        var agent = AgentDefinitionLoader.LoadAgentFromJson(
            $$"""
            {
              "kind": "prompt",
              "name": "trusted-mcp-agent",
              "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
              "metadata": { "trust-profile": "constrained-local" },
              "tools": [{
                "kind": "mcp",
                "name": "trusted-mcp",
                "serverName": "trusted-mcp",
                "connection": { "kind": "Anonymous", "endpoint": "{{endpoint}}" }
              }]
            }
            """);
        var profileProvider = new DictionaryTrustProfileProvider(
            new Dictionary<string, TrustProfileEntity>(StringComparer.Ordinal)
            {
                ["constrained-local"] = new TrustProfileEntity
                {
                    Name = "constrained-local",
                    Definition = new TrustProfileDefinition
                    {
                        HostingWorkspacesClientInstances = ["."],
                        NetworkCapabilities = [],
                    },
                },
            });
        var executor = new RecordingExecutor();

        await using var chat = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = agent,
            TrustProfileProvider = profileProvider,
            AgentServices = new AgentServices
            {
                ProcessExecutor = executor,
                TrustProfilePolicyCompiler = new MxcTrustProfilePolicyCompiler(new FakeMxcPolicyHost()),
            },
        });
        await chat.Initialization;

        Assert.NotNull(executor.LastRequest);
        Assert.NotNull(executor.LastRequest!.MxcPolicy);
    }

    [Fact]
    public async Task ResolveExecutionTrustContext_PreservesObservedProfileRevision()
    {
        var agent = CreateEchoAgent(LocalOnlyProfileName);
        var provider = new VersionedProvider(CreateProvider(), "revision-42");

        var context = await AgentFactory.ResolveExecutionTrustContextAsync(
            agent,
            provider,
            new AgentServices
            {
                TrustProfilePolicyCompiler = new MxcTrustProfilePolicyCompiler(new FakeMxcPolicyHost()),
            },
            CancellationToken.None);

        Assert.NotNull(context);
        Assert.Equal(LocalOnlyProfileName, context!.RemoteReference!.Id);
        Assert.Equal("revision-42", context.RemoteReference.ExpectedRevision);
    }

    private sealed class RecordingExecutor : IProcessExecutor
    {
        private readonly ProcessExecutor inner = new();

        public ProcessExecutionRequest? LastRequest { get; private set; }

        public IProcessHandle Start(ProcessExecutionRequest request)
        {
            LastRequest = request;
            return inner.Start(request with { MxcPolicy = null });
        }
    }

    private sealed class VersionedProvider(
        ITrustProfileProvider inner,
        string revision) : IVersionedTrustProfileProvider
    {
        public ValueTask<TrustProfile> ResolveAsync(
            string profileName,
            CancellationToken cancellationToken = default)
            => inner.ResolveAsync(profileName, cancellationToken);

        public async ValueTask<VersionedTrustProfile> ResolveVersionedAsync(
            string profileName,
            CancellationToken cancellationToken = default)
            => new(await inner.ResolveAsync(profileName, cancellationToken), revision);
    }
}

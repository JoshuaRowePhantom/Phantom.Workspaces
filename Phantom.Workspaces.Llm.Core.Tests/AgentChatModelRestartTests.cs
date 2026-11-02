using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Copilot;
using Phantom.Workspaces.Llm.Core.Tests.Infrastructure;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.SlashCommands;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class AgentChatModelRestartTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RestartSession_ModelSelection_UsesNewProductionClientBeforeFirstCreateOrResume(
        bool durableRecovery,
        bool resume)
    {
        var sessions = new List<FakeCopilotSession>();
        var backendFactory = new ModelBackendFactory(() =>
            {
                var session = new FakeCopilotSession();
                sessions.Add(session);
                return new FakeCopilotClient(session);
            });
        var services = new AgentServices { CopilotClientFactory = backendFactory };
        var store = new InMemoryAgentPersistenceStore();
        await using var factory = new AgentChatFactory(store, services, TaskScheduler.Default);
        var sessionId = new AgentSessionId("model-restart");
        var definition = AgentDefinitionLoader.LoadAgentFromJson("""
            { "kind": "prompt", "name": "model-restart",
              "model": { "id": "model-a", "provider": "github-copilot", "apiType": "OpenAI" } }
            """);
        await using var original = await factory.GetOrCreateAsync(sessionId, definition);
        var oldClient = Client(original.LocalAgentChat);
        await EnsureSessionAsync(oldClient);
        var handler = new CopilotSdkModelSlashCommandHandler(oldClient);
        await handler.ExecuteAsync(new SlashCommandContext { AgentChat = original.AgentChat },
            "model-b", CancellationToken.None);
        Assert.Equal("model-b", sessions[0].LastModelSet);
        AgentRuntimeQueueCheckpoint? checkpoint = null;
        await using var replacement = await factory.RestartExactAsync(
            sessionId, original.AgentChat, null,
            async (value, _) =>
            {
                checkpoint = JsonSerializer.Deserialize<AgentRuntimeQueueCheckpoint>(
                    JsonSerializer.Serialize(value, AIJsonUtilities.DefaultOptions), AIJsonUtilities.DefaultOptions);
                await Assert.ThrowsAsync<InvalidOperationException>(() => handler.ExecuteAsync(
                    new SlashCommandContext { AgentChat = original.AgentChat }, "model-c", CancellationToken.None));
            }, CancellationToken.None);
        Assert.Equal("model-b", checkpoint!.ModelId);
        var newClient = Client(replacement.LocalAgentChat);
        Assert.NotSame(oldClient, newClient);
        Assert.Equal("model-b", newClient.ModelId);
        if (durableRecovery)
        {
            await using var recoveredFactory = new AgentChatFactory(store, services, TaskScheduler.Default);
            await using var recovered = await recoveredFactory.GetOrCreateAsync(
                sessionId, null, null, null, null, true, CancellationToken.None, checkpoint);
            newClient = Client(recovered.LocalAgentChat);
            Assert.NotSame(Client(replacement.LocalAgentChat), newClient);
            await VerifyFirstSessionAsync(newClient, resume, sessions);
        }
        else
            await VerifyFirstSessionAsync(newClient, resume, sessions);
        Assert.Equal("model-a", AgentFactory.GetModelId(definition));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartSession_OverlappingModelMutation_RejectsHandoffUntilMutationFinishes(bool fail)
    {
        var session = new FakeCopilotSession();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.SetModelHandler = async (_, _) =>
            {
                entered.TrySetResult();
                await release.Task;
                if (fail)
                    throw new IOException("Backend rejected model.");
            };
        var backendFactory = new ModelBackendFactory(() => new FakeCopilotClient(session));
        await using var factory = new AgentChatFactory(new InMemoryAgentPersistenceStore(),
            new AgentServices { CopilotClientFactory = backendFactory }, TaskScheduler.Default);
        await using var lease = await factory.GetOrCreateAsync(new AgentSessionId("model-race"),
            AgentDefinitionLoader.LoadAgentFromJson("""
                { "kind": "prompt", "name": "model-race",
                  "model": { "id": "model-a", "provider": "github-copilot", "apiType": "OpenAI" } }
                """));
        var client = Client(lease.LocalAgentChat);
        await EnsureSessionAsync(client);
        var changing = client.SetModelIdAsync("model-b", CancellationToken.None);
        await entered.Task;
        try
        {
            Assert.Throws<InvalidOperationException>(() => lease.LocalAgentChat.BeginRuntimeHandoff());
            Assert.False(lease.LocalAgentChat.InputQueueManager.IsRuntimeHandoff);
        }
        finally { release.TrySetResult(); }
        if (fail)
            await Assert.ThrowsAsync<IOException>(() => changing);
        else
            await changing;
        Assert.Equal(fail ? "model-a" : "model-b", lease.LocalAgentChat.BeginRuntimeHandoff().ModelId);
        lease.LocalAgentChat.RollbackRuntimeHandoff();
        session.SetModelHandler = null;
        await client.SetModelIdAsync("model-c", CancellationToken.None);
        Assert.Equal("model-c", lease.LocalAgentChat.BeginRuntimeHandoff().ModelId);
    }

    private static CopilotSdkChatClient Client(AgentChat chat)
        => Assert.IsType<CopilotSdkChatClient>(((IChatClient)typeof(AgentChat)
            .GetField("client", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(chat)!)
            .GetService(typeof(CopilotSdkChatClient)));

    private static async Task VerifyFirstSessionAsync(
        CopilotSdkChatClient client, bool resume, List<FakeCopilotSession> sessions)
    {
        typeof(CopilotSdkChatClient).GetField("pendingResumeSessionId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, resume ? "persisted-sdk-session" : null);
        await EnsureSessionAsync(client);
        if (resume)
            Assert.Equal("model-b", Assert.Single(sessions[^1].ResumeSessionCalls).Config.Model);
        else
            Assert.Equal("model-b", Assert.Single(sessions[^1].CreateSessionConfigs).Model);
    }

    private static Task EnsureSessionAsync(CopilotSdkChatClient client)
        => (Task)typeof(CopilotSdkChatClient).GetMethod("EnsureSessionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(client, [null, CancellationToken.None])!;

    private sealed class ModelBackendFactory(Func<ICopilotClient> create) : ICopilotClientFactory
    {
        public ICopilotClient Create(GitHub.Copilot.CopilotClientOptions options) => create();
    }
}

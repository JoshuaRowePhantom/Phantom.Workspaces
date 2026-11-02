using System.Reflection;
using System.Text.Json;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using Moq;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Copilot;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Llm.SlashCommands;

namespace Phantom.Workspaces.Tests;

public sealed partial class DataAccessAgentSessionRuntimeHostFactoryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RestartSession_DurableModelSelection_NewOwnerClientUsesSelectedModel(
        bool recoverStoppedHost, bool resume)
    {
        var creates = new List<SessionConfig>();
        var resumes = new List<ResumeSessionConfig>();
        var services = new AgentServices { CopilotClientFactory = new ModelRestartBackend(creates, resumes) };
        var environment = new RestartEnvironment(runtimeServices: services);
        try
        {
            environment.ChangeAuthority(new Dictionary<string, JsonElement>
            {
                ["definition"] = JsonSerializer.SerializeToElement(new
                {
                    kind = "prompt", name = "sdk-owner",
                    model = new { id = "model-a", provider = "github-copilot", apiType = "OpenAI" },
                }),
            });
            await using var viewer = await environment.AcquireAsync();
            var old = ModelClient(Assert.IsType<AgentChat>(viewer.AgentChat));
            await EnsureModelSessionAsync(old);
            Assert.Equal("model-a", Assert.Single(creates).Model);
            await new CopilotSdkModelSlashCommandHandler(old).ExecuteAsync(
                new SlashCommandContext { AgentChat = viewer.AgentChat }, "model-b", TestContext.Current.CancellationToken);
            var request = new AgentSessionOwnerRestartRequest
            {
                CommandId = Guid.NewGuid(), ExpectedRuntime = viewer.RestartIdentity!,
            };
            if (recoverStoppedHost)
                environment.BeforePublication = _ => throw new IOException("Startup cannot publish.");
            var result = await viewer.RestartRuntimeAsync!(request, TestContext.Current.CancellationToken);
            var journal = environment.Persisted.Data!.Value.GetProperty("restart-operations");
            var operations = JsonSerializer.Deserialize<Dictionary<string,
                Services.AgentSessions.DataAccessAgentSessionRuntimeHostFactory.RestartOperation>>(journal, AIJsonUtilities.DefaultOptions)!;
            Assert.Equal("model-b", operations[request.CommandId.ToString("D")].Checkpoint.ModelId);
            if (recoverStoppedHost)
            {
                Assert.Equal(AgentSessionOwnerRestartState.Stopped, result.State);
                var store = environment.Store;
                var persisted = environment.Persisted;
                await viewer.DisposeAsync();
                await environment.DisposeAsync();
                environment = new RestartEnvironment(store, persisted, runtimeServices: services);
                environment.Time.Advance(TimeSpan.FromMinutes(2));
                result = await environment.Host.RestartAsync(RestartPeer(), request, TestContext.Current.CancellationToken);
            }
            Assert.Equal(AgentSessionOwnerRestartState.Succeeded, result.State);
            var current = (await environment.Registry.TryGetAsync("session", 3, TestContext.Current.CancellationToken))!;
            var replacement = ModelClient(Assert.IsType<AgentChat>(current.Chat));
            Assert.NotSame(old, replacement);
            Assert.Equal("model-b", replacement.ModelId);
            typeof(CopilotSdkChatClient).GetField("pendingResumeSessionId", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(replacement, resume ? "sdk-conversation" : null);
            await EnsureModelSessionAsync(replacement);
            if (resume)
                Assert.Equal("model-b", Assert.Single(resumes).Model);
            else
                Assert.Equal("model-b", creates[^1].Model);
            Assert.Single(environment.Table.RunningSessions);
        }
        finally { await environment.DisposeAsync(); }
    }

    private static CopilotSdkChatClient ModelClient(AgentChat chat)
        => Assert.IsType<CopilotSdkChatClient>(((IChatClient)typeof(AgentChat)
            .GetField("client", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(chat)!)
            .GetService(typeof(CopilotSdkChatClient)));

    private static Task EnsureModelSessionAsync(CopilotSdkChatClient client)
        => (Task)typeof(CopilotSdkChatClient).GetMethod("EnsureSessionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(client, [null, TestContext.Current.CancellationToken])!;

    private sealed class ModelRestartBackend(List<SessionConfig> creates, List<ResumeSessionConfig> resumes)
        : ICopilotClientFactory
    {
        public ICopilotClient Create(CopilotClientOptions options) => new Backend(creates, resumes);

        private sealed class Backend(List<SessionConfig> creates, List<ResumeSessionConfig> resumes) : ICopilotClient
        {
            public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
                => Task.FromResult<IReadOnlyList<ModelInfo>>([]);
            public Task<ICopilotSession> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken)
            {
                creates.Add(config);
                return Task.FromResult<ICopilotSession>(new Session());
            }
            public Task<ICopilotSession> ResumeSessionAsync(string sessionId, ResumeSessionConfig config, CancellationToken cancellationToken)
            {
                resumes.Add(config);
                return Task.FromResult<ICopilotSession>(new Session());
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private sealed class Session : ICopilotSession, IDisposable
        {
            public string SessionId => "sdk-conversation";
            public IDisposable Subscribe(Action<SessionEvent> handler) => this;
            public Task<AssistantMessageEvent?> SendAndWaitAsync(MessageOptions options, TimeSpan? timeout, CancellationToken cancellationToken)
                => Task.FromResult<AssistantMessageEvent?>(null);
            public Task SendAsync(MessageOptions options, CancellationToken cancellationToken) => Task.CompletedTask;
            public Task AbortAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public Task SetModelAsync(string modelId, CancellationToken cancellationToken) => Task.CompletedTask;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
            public void Dispose() { }
        }
    }
}

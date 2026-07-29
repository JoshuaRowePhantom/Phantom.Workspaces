using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AgentSchema;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm.Core.Tests;

/// <summary>
/// Shared fakes for tests that exercise the unified factory-path sub-agent routing (issues #1109
/// and #1110). Extracted from CopilotSdkChatClientSubAgentFactoryTests so
/// CopilotSubAgentRouterTests and CopilotSdkEventPipelineTests can reuse them without
/// duplicating the reflection wiring for ChatClientAgent.
/// </summary>
internal static class SubAgentTestFakes
{
    internal sealed class FakeRunningAgentChatFactory : IRunningAgentChatFactory
    {
        private readonly bool _exposeReceiver;

        public List<(AgentDefinition Definition, AgentSessionId SessionId)> CreateCalls { get; } = new();

        /// <summary>
        /// Records the (displayNameOverride, descriptionOverride) tuple for each
        /// <c>CreateAsync</c> call so tests can assert on the values the router propagates from
        /// the sub-agent-started lifecycle arguments (Issue #1133).
        /// </summary>
        public List<(string? DisplayNameOverride, string? DescriptionOverride)> CreateCallOverrides { get; } = new();

        public RunningAgentChatLease? CreatedLease { get; private set; }
        public CopilotSubAgentChatClient? CreatedReceiver { get; private set; }

        public System.Collections.ObjectModel.ObservableCollection<RunningAgentChat> RunningSessions { get; } = new();

        public FakeRunningAgentChatFactory(bool exposeReceiver = true)
        {
            _exposeReceiver = exposeReceiver;
        }

        public void ResetLease()
        {
            CreatedLease = null;
            CreatedReceiver = null;
        }

        Task<RunningAgentChatLease> IRunningAgentChatFactory.CreateAsync(
            AgentDefinition definition,
            AgentSessionId sessionId,
            AgentServices? services,
            string? displayNameOverride,
            string? descriptionOverride,
            CancellationToken ct)
        {
            CreateCalls.Add((definition, sessionId));
            CreateCallOverrides.Add((displayNameOverride, descriptionOverride));

            IChatClient client;
            if (_exposeReceiver)
            {
                var receiver = new CopilotSubAgentChatClient();
                CreatedReceiver = receiver;
                client = receiver;
            }
            else
            {
                client = new NonReceiverChatClient();
            }

            var chat = new AgentChat(new InternalCreateAgentChatRequest
            {
                AgentDefinition = null,
                ConfiguredStore = new InMemoryAgentPersistenceStore(),
                DisplayNameOverride = displayNameOverride,
                DescriptionOverride = descriptionOverride,
            });

            var chatClientAgent = new ChatClientAgent(client, new ChatClientAgentOptions
            {
                UseProvidedChatClientAsIs = true,
            });
            var chatClientAgentField = typeof(AgentChat).GetField("chatClientAgent",
                BindingFlags.NonPublic | BindingFlags.Instance);
            chatClientAgentField!.SetValue(chat, chatClientAgent);

            var lease = new RunningAgentChatLease(sessionId, chat, () => ValueTask.CompletedTask);
            CreatedLease = lease;
            return Task.FromResult(lease);
        }

        Task<RunningAgentChatLease> IRunningAgentChatFactory.GetAsync(AgentSessionId sessionId, CancellationToken ct) =>
            throw new NotImplementedException();

        Task<RunningAgentChatLease> IRunningAgentChatFactory.GetOrCreateAsync(
            AgentSessionId sessionId,
            AgentDefinition? definition,
            AgentServices? services,
            string? displayNameOverride,
            string? descriptionOverride,
            CancellationToken ct) =>
            throw new NotImplementedException();
    }

    private sealed class NonReceiverChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }

    internal sealed class FakeSubAgentTable : ISubAgentTable
    {
        public List<AgentChat> AddedChats { get; } = new();

        Task<SubAgent> ISubAgentTable.Add(AgentChat agentChat)
        {
            AddedChats.Add(agentChat);
            var sessionId = new AgentSessionId(agentChat.AgentSessionId);
            return Task.FromResult(new SubAgent(sessionId, agentChat, null));
        }
    }
}

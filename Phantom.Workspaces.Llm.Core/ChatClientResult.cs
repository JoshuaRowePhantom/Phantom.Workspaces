using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// The result of <see cref="AgentFactory.CreateChatClient(AgentDefinition, AgentServices?, AgentInputQueueManager?)"/>:
/// the resolved <see cref="IChatClient"/> (optionally wrapped with steering middleware) and a
/// human-readable display name.
/// </summary>
public sealed record ChatClientResult(IChatClient ChatClient, string DisplayName);

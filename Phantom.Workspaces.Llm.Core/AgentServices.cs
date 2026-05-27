using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

namespace Phantom.Workspaces.Llm;

public sealed class AgentServices
{
    public bool LogChat { get; init; }

    public bool LogHttpRequests { get; init; }

    public ILoggerFactory? LoggerFactory { get; init; }

    public ChatHistoryProvider? ChatHistoryProvider { get; init; }
}

using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm;

public sealed record SessionInputEvent
{
    public required ChatMessage[] Messages { get; init; }

    public bool InterruptCurrentResponse { get; init; }
}

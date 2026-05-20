namespace Phantom.Workspaces.Llm;

public sealed record SessionInputEvent
{
    public required LlmEvent[] LlmEvents { get; init; }

    public bool InterruptCurrentResponse { get; init; }
}

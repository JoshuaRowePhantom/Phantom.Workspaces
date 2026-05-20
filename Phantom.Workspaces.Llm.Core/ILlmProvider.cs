namespace Phantom.Workspaces.Llm;

public interface ILlmProvider
{
    IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        LlmConversation conversation,
        CancellationToken cancellationToken = default);
}

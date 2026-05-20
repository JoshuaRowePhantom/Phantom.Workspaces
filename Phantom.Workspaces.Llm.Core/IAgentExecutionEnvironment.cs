namespace Phantom.Workspaces.Llm;

public interface IAgentExecutionEnvironment
{
    Task<LlmEvent> ExecuteToolCallAsync(
        LlmEvent toolCall,
        CancellationToken cancellationToken = default);
}

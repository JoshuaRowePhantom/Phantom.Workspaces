using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm;

public interface ISubAgentChat
{
    void Push(ChatResponseUpdate update);
    void Complete();
    void Fail(Exception ex);
}

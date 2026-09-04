using GitHub.Copilot;

namespace Phantom.Workspaces.Llm.Copilot;

internal interface ICopilotClientFactory
{
    ICopilotClient Create(CopilotClientOptions options);
}

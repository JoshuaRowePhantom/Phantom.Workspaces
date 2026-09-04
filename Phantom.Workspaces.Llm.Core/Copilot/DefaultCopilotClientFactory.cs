using GitHub.Copilot;

namespace Phantom.Workspaces.Llm.Copilot;

internal sealed class DefaultCopilotClientFactory : ICopilotClientFactory
{
    public static DefaultCopilotClientFactory Instance { get; } = new();

    private DefaultCopilotClientFactory() { }

    public ICopilotClient Create(CopilotClientOptions options) =>
        new RealCopilotClientAdapter(new CopilotClient(options));
}

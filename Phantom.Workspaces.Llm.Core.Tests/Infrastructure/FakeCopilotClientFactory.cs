using GitHub.Copilot;
using Phantom.Workspaces.Llm.Copilot;

namespace Phantom.Workspaces.Llm.Core.Tests.Infrastructure;

internal sealed class FakeCopilotClientFactory : ICopilotClientFactory
{
    private readonly FakeCopilotClient fakeClient;

    public FakeCopilotClientFactory(FakeCopilotClient fakeClient)
    {
        this.fakeClient = fakeClient ?? throw new ArgumentNullException(nameof(fakeClient));
    }

    public ICopilotClient Create(CopilotClientOptions options) => this.fakeClient;
}

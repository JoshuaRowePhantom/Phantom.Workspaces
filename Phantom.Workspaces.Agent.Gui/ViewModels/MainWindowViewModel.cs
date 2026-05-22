using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly IChatClient chatClient;
    private readonly ILoggerFactory? loggerFactory;

    public MainWindowViewModel(AgentDefinitionParseResult parseResult)
    {
        this.loggerFactory = (parseResult.LogChat || parseResult.LogHttpRequests)
            ? NullLoggerFactory.Instance
            : null;

        var services = new AgentServices
        {
            LogChat = parseResult.LogChat,
            LogHttpRequests = parseResult.LogHttpRequests,
            LoggerFactory = this.loggerFactory,
        };

        var created = AgentFactory.CreateAgentChat(parseResult.AgentDefinition, services);
        this.chatClient = created.Client;

        var displayName = created.DisplayName;
        if (!string.IsNullOrEmpty(parseResult.AgentSchemaPath))
        {
            displayName = $"{displayName} [from {Path.GetFileName(parseResult.AgentSchemaPath)}]";
        }

        this.Agent = new AgentViewModel(created.Chat, displayName);
    }

    public AgentViewModel Agent { get; }

    public async ValueTask DisposeAsync()
    {
        await this.Agent.DisposeAsync();
        this.chatClient.Dispose();
        this.loggerFactory?.Dispose();
    }
}

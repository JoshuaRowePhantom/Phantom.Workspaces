using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase, IAsyncDisposable
{
    public MainWindowViewModel(AgentDefinitionParseResult parseResult)
    {
        var loggerFactory = new ObservableLoggerFactory();
        this.LoggerFactory = loggerFactory;

        var chat = AgentFactory.CreateAgentChat(parseResult.AgentDefinition, CreateServices(parseResult, loggerFactory));
        this.Agent = CreateAgentViewModel(parseResult, chat);
    }

    public AgentViewModel Agent { get; }

    public ObservableLoggerFactory LoggerFactory { get; }

    public async ValueTask DisposeAsync()
    {
        await this.Agent.DisposeAsync();
        this.LoggerFactory.Dispose();
    }

    private static AgentServices CreateServices(
        AgentDefinitionParseResult parseResult,
        ObservableLoggerFactory loggerFactory)
    {
        return new AgentServices
        {
            LogChat = parseResult.LogChat,
            LogHttpRequests = parseResult.LogHttpRequests,
            LoggerFactory = parseResult.LogChat || parseResult.LogHttpRequests ? loggerFactory : null,
        };
    }

    private static AgentViewModel CreateAgentViewModel(
        AgentDefinitionParseResult parseResult,
        AgentChat chat)
    {
        var displayName = chat.DisplayName;
        if (!string.IsNullOrEmpty(parseResult.AgentSchemaPath))
        {
            displayName = $"{displayName} [from {Path.GetFileName(parseResult.AgentSchemaPath)}]";
        }

        return new AgentViewModel(chat, displayName);
    }
}

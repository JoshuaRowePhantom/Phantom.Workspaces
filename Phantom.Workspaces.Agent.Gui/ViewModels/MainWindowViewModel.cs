using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase, IAsyncDisposable
{
    private MainWindowViewModel(AgentChat chat, AgentDefinitionParseResult parseResult, ObservableLoggerFactory loggerFactory)
    {
        this.LoggerFactory = loggerFactory;
        this.Agent = CreateAgentViewModel(parseResult, chat);
    }

    public static Task<MainWindowViewModel> CreateAsync(AgentDefinitionParseResult parseResult)
        => CreateAsync(parseResult, agentServicesOverride: null);

    public static async Task<MainWindowViewModel> CreateAsync(
        AgentDefinitionParseResult parseResult,
        AgentServices? agentServicesOverride)
    {
        var loggerFactory = new ObservableLoggerFactory();
        var services = agentServicesOverride ?? CreateServices(parseResult, loggerFactory);
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentSessionId = parseResult.AgentSessionId,
                AgentDefinition = parseResult.AgentDefinition,
                AgentServices = services,
            });
        return new MainWindowViewModel(chat, parseResult, loggerFactory);
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
            ToolsetFactory = ToolsetFactory.CreateDefaultToolsetFactory(),
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

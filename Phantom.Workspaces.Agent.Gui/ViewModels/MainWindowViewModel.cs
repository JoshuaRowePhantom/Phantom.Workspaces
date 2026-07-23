using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase, IAsyncDisposable
{
    private MainWindowViewModel(AgentChat chat, AgentDefinitionParseResult parseResult, ObservableLoggerFactory loggerFactory, TaskScheduler foregroundScheduler)
    {
        this.LoggerFactory = loggerFactory;
        this.Agent = CreateAgentViewModel(parseResult, chat, loggerFactory, foregroundScheduler);
    }

    public static Task<MainWindowViewModel> CreateAsync(AgentDefinitionParseResult parseResult)
        => CreateAsync(parseResult, agentServicesOverride: null);

    public static async Task<MainWindowViewModel> CreateAsync(
        AgentDefinitionParseResult parseResult,
        AgentServices? agentServicesOverride)
    {
        // #1122: Capture the UI-thread scheduler synchronously (before any await) so that it
        // truly reflects the calling thread's SynchronizationContext. Callers invoke this on
        // the UI thread in production. In test contexts without a SynchronizationContext, we
        // fall back to TaskScheduler.Default — those tests do not exercise the UI-affine
        // sub-agent restore path that #1122 addresses.
        var foregroundScheduler = SynchronizationContext.Current is not null
            ? (TaskScheduler)SynchronizationContextTaskScheduler.FromCurrent()
            : TaskScheduler.Default;
        var loggerFactory = new ObservableLoggerFactory();
        var services = agentServicesOverride ?? CreateServices(parseResult, loggerFactory);
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentSessionId = parseResult.AgentSessionId,
                AgentDefinition = parseResult.AgentDefinition,
                AgentServices = services,
            });
        return new MainWindowViewModel(chat, parseResult, loggerFactory, foregroundScheduler);
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
        AgentChat chat,
        ObservableLoggerFactory loggerFactory,
        TaskScheduler foregroundScheduler)
    {
        var displayName = chat.DisplayName;
        if (!string.IsNullOrEmpty(parseResult.AgentSchemaPath))
        {
            displayName = $"{displayName} [from {Path.GetFileName(parseResult.AgentSchemaPath)}]";
        }

        return new AgentViewModel(chat, displayName, chat.Description, loggerFactory, foregroundScheduler)
        {
            OpenUrlHandler = OpenUrlExternal,
        };
    }

    private static void OpenUrlExternal(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Best-effort; no notification surface available.
        }
    }
}

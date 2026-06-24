using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm.Interfaces;
using System.Text.Json;

namespace Phantom.Workspaces.Llm;

using CreateToolsetFunction = System.Func<AgentSchema.Tool, AgentServices, System.Threading.Tasks.Task<Microsoft.Agents.AI.AIContextProvider?>>;

public sealed class ToolsetFactory : IToolsetFactory
{
    private readonly CreateToolsetFunction createToolset;

    private ToolsetFactory(CreateToolsetFunction createToolset)
    {
        this.createToolset = createToolset;
    }

    public async Task<AIContextProvider?> CreateToolsetAsync(
        AgentSchema.Tool tool,
        AgentServices agentServices)
    {
        return await createToolset(tool, agentServices);
    }

    public static IToolsetFactory CreateNamedToolsetFactory(
        string kind,
        CreateToolsetFunction createToolsetAsync,
        IToolsetFactory? underlyingInstance = null)
    {
        var resolvedUnderlyingInstance = underlyingInstance ?? EmptyToolsetFactory.Instance;

        return new ToolsetFactory(
            async (tool, agentServices) =>
            {
                if (string.Equals(tool.Kind, kind, StringComparison.Ordinal))
                {
                    return await createToolsetAsync(tool, agentServices);
                }

                return await resolvedUnderlyingInstance.CreateToolsetAsync(tool, agentServices);
            });
    }

    public static AIContextProvider CreateFixedToolset(
        params AITool[] tools)
    {
        return new FixedToolsContextProvider(tools);
    }

    public static IToolsetFactory CreateWebSearchToolsetFactory(
        IToolsetFactory? underlyingToolsetFactory = null)
    {
        return CreateNamedToolsetFactory("web_search", CreateWebSearchToolsetAsync, underlyingToolsetFactory);
    }

    public static IToolsetFactory CreateWebRequestToolsetFactory(IToolsetFactory? underlyingToolsetFactory = null)
    {
        return CreateNamedToolsetFactory("web_request", CreateWebRequestToolsetAsync, underlyingToolsetFactory);
    }

    public static IToolsetFactory CreateWebToolsetFactory(IToolsetFactory? underlyingToolsetFactory = null)
    {
        return CreateNamedToolsetFactory("web", CreateWebToolsetAsync, underlyingToolsetFactory);
    }

    public static IToolsetFactory CreateFilesystemToolsetFactory(IToolsetFactory? underlyingToolsetFactory = null)
    {
        return CreateNamedToolsetFactory("filesystem", CreateFilesystemToolsetAsync, underlyingToolsetFactory);
    }

    public static IToolsetFactory CreateWorkspaceEntityToolsetFactory(
        IDataAccessLayer dataAccessLayer,
        IToolsetFactory? underlyingToolsetFactory = null)
    {
        return CreateNamedToolsetFactory(
            "workspace-entity",
            (tool, agentServices) =>
            {
                _ = tool;
                _ = agentServices;
                return Task.FromResult<AIContextProvider?>(new WorkspaceEntityContextProvider(dataAccessLayer));
            },
            underlyingToolsetFactory);
    }

    public static IToolsetFactory CreateCurrentSessionToolsetFactory(
        IDataAccessLayer dataAccessLayer,
        CurrentSessionContext currentSessionContext,
        IToolsetFactory? underlyingToolsetFactory = null)
    {
        return CreateNamedToolsetFactory(
            "current-session",
            (tool, agentServices) =>
            {
                _ = tool;
                _ = agentServices;
                return Task.FromResult<AIContextProvider?>(
                    new CurrentSessionContextProvider(dataAccessLayer, currentSessionContext));
            },
            underlyingToolsetFactory);
    }

    private static Task<AIContextProvider?> CreateWebSearchToolsetAsync(
        AgentSchema.Tool tool,
        AgentServices agentServices)
    {
        _ = tool;
        return Task.FromResult<AIContextProvider?>(CreateFixedToolset(
            new WebSearchTool(logger: agentServices.LoggerFactory?.CreateLogger<WebSearchTool>())));
    }

    private static Task<AIContextProvider?> CreateWebRequestToolsetAsync(
        AgentSchema.Tool tool,
        AgentServices agentServices)
    {
        _ = tool;
        return Task.FromResult<AIContextProvider?>(CreateFixedToolset(
            new WebRequestTool(logger: agentServices.LoggerFactory?.CreateLogger<WebRequestTool>())));
    }

    private static Task<AIContextProvider?> CreateWebToolsetAsync(
        AgentSchema.Tool tool,
        AgentServices agentServices)
    {
        _ = tool;
        return Task.FromResult<AIContextProvider?>(CreateFixedToolset(
            new WebSearchTool(logger: agentServices.LoggerFactory?.CreateLogger<WebSearchTool>()),
            new WebRequestTool(logger: agentServices.LoggerFactory?.CreateLogger<WebRequestTool>())));
    }

    private static Task<AIContextProvider?> CreateFilesystemToolsetAsync(
        AgentSchema.Tool tool,
        AgentServices agentServices)
    {
        var connection = (tool as AgentSchema.CustomTool)?.Connection;
        var connectionJson = connection switch
        {
            null => null,
            _ => JsonSerializer.Serialize(connection),
        };

        AIContextProvider toolset = new FilesystemServiceContextProvider(
            editStoreConnectionJson: connectionJson,
            loggerFactory: agentServices.LoggerFactory);
        return Task.FromResult<AIContextProvider?>(toolset);
    }

    public static IToolsetFactory Combine(params IToolsetFactory[] factories)
    {
        return new ToolsetFactory(
            async (tool, agentServices) =>
            {
                foreach (var factory in factories)
                {
                    var toolset = await factory.CreateToolsetAsync(tool, agentServices);
                    if (toolset is not null)
                    {
                        return toolset;
                    }
                }

                return null;
            });
    }

    public static IToolsetFactory CreateDefaultToolsetFactory(IToolsetFactory? underlyingToolsetFactory = null)
    {
        var resolvedUnderlyingToolsetFactory = underlyingToolsetFactory ?? EmptyToolsetFactory.Instance;
        return Combine(
            CreateWebSearchToolsetFactory(resolvedUnderlyingToolsetFactory),
            CreateWebRequestToolsetFactory(resolvedUnderlyingToolsetFactory),
            CreateWebToolsetFactory(resolvedUnderlyingToolsetFactory),
            CreateFilesystemToolsetFactory(resolvedUnderlyingToolsetFactory));
    }

    public static IToolsetFactory CreateEmptyToolsetFactory() => EmptyToolsetFactory.Instance;

    private sealed class EmptyToolsetFactory : IToolsetFactory
    {
        public static readonly EmptyToolsetFactory Instance = new();

        private EmptyToolsetFactory()
        {
        }

        public Task<AIContextProvider?> CreateToolsetAsync(
            AgentSchema.Tool tool,
            AgentServices agentServices)
        {
            _ = tool;
            _ = agentServices;
            return Task.FromResult<AIContextProvider?>(null);
        }
    }
}

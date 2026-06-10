using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Llm.Interfaces;
using System.Text.Json;

namespace Phantom.Workspaces.Llm;

public sealed class ToolsetFactory : IToolsetFactory
{
    private readonly IReadOnlyDictionary<string, Func<Dictionary<string, object>, AgentServices, Task<IToolset>>> createToolsetByName;

    private ToolsetFactory(
        IReadOnlyDictionary<string, Func<Dictionary<string, object>, AgentServices, Task<IToolset>>> createToolsetByName)
    {
        this.createToolsetByName = createToolsetByName;
    }

    public Task<IToolset> CreateToolsetAsync(
        string name,
        Dictionary<string, object> properties,
        AgentServices agentServices)
    {
        _ = agentServices;
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Toolset name is required.", nameof(name));
        }

        if (!this.createToolsetByName.TryGetValue(name, out var createToolset))
        {
            throw new InvalidOperationException($"No toolset factory is registered for '{name}'.");
        }

        return createToolset(properties, agentServices);
    }

    public static IToolsetFactory CreateNamedToolsetFactory(
        string name,
        Func<string, Dictionary<string, object>, AgentServices, Task<IToolset>> createToolsetAsync,
        IToolsetFactory underlyingInstance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(createToolsetAsync);
        ArgumentNullException.ThrowIfNull(underlyingInstance);

        var normalizedName = name.Trim();
        var map = new Dictionary<string, Func<Dictionary<string, object>, AgentServices, Task<IToolset>>>(StringComparer.OrdinalIgnoreCase)
        {
            [normalizedName] = (properties, agentServices) =>
            {
                _ = underlyingInstance;
                return createToolsetAsync(normalizedName, properties ?? [], agentServices);
            },
        };
        return new ToolsetFactory(map);
    }

    public static IToolsetFactory CreateWebSearchToolsetFactory(IToolsetFactory? underlyingToolsetFactory = null)
    {
        var resolvedUnderlyingToolsetFactory = underlyingToolsetFactory ?? EmptyToolsetFactory.Instance;
        return CreateNamedToolsetFactory("web_search", CreateWebSearchToolsetAsync, resolvedUnderlyingToolsetFactory);
    }

    public static IToolsetFactory CreateWebRequestToolsetFactory(IToolsetFactory? underlyingToolsetFactory = null)
    {
        var resolvedUnderlyingToolsetFactory = underlyingToolsetFactory ?? EmptyToolsetFactory.Instance;
        return CreateNamedToolsetFactory("web_request", CreateWebRequestToolsetAsync, resolvedUnderlyingToolsetFactory);
    }

    public static IToolsetFactory CreateWebToolsetFactory(IToolsetFactory? underlyingToolsetFactory = null)
    {
        var resolvedUnderlyingToolsetFactory = underlyingToolsetFactory ?? EmptyToolsetFactory.Instance;
        return CreateNamedToolsetFactory("web", CreateWebToolsetAsync, resolvedUnderlyingToolsetFactory);
    }

    public static IToolsetFactory CreateFilesystemToolsetFactory(IToolsetFactory? underlyingToolsetFactory = null)
    {
        var resolvedUnderlyingToolsetFactory = underlyingToolsetFactory ?? EmptyToolsetFactory.Instance;
        return CreateNamedToolsetFactory("filesystem", CreateFilesystemToolsetAsync, resolvedUnderlyingToolsetFactory);
    }

    private static Task<IToolset> CreateWebSearchToolsetAsync(
        string name,
        Dictionary<string, object> properties,
        AgentServices agentServices)
    {
        _ = name;
        _ = properties;
        IToolset toolset = new FixedToolset(
        [
            new WebSearchTool(logger: agentServices.LoggerFactory?.CreateLogger<WebSearchTool>()),
        ]);
        return Task.FromResult(toolset);
    }

    private static Task<IToolset> CreateWebRequestToolsetAsync(
        string name,
        Dictionary<string, object> properties,
        AgentServices agentServices)
    {
        _ = name;
        _ = properties;
        IToolset toolset = new FixedToolset(
        [
            new WebRequestTool(logger: agentServices.LoggerFactory?.CreateLogger<WebRequestTool>()),
        ]);
        return Task.FromResult(toolset);
    }

    private static Task<IToolset> CreateWebToolsetAsync(
        string name,
        Dictionary<string, object> properties,
        AgentServices agentServices)
    {
        _ = name;
        _ = properties;
        IToolset toolset = new FixedToolset(
        [
            new WebSearchTool(logger: agentServices.LoggerFactory?.CreateLogger<WebSearchTool>()),
            new WebRequestTool(logger: agentServices.LoggerFactory?.CreateLogger<WebRequestTool>()),
        ]);
        return Task.FromResult(toolset);
    }

    private static Task<IToolset> CreateFilesystemToolsetAsync(
        string name,
        Dictionary<string, object> properties,
        AgentServices agentServices)
    {
        _ = name;
        properties.TryGetValue("connection", out var connection);
        var connectionJson = connection switch
        {
            null => null,
            JsonElement jsonElement => jsonElement.GetRawText(),
            _ => JsonSerializer.Serialize(connection),
        };

        IToolset toolset = new FilesystemServiceToolset(
            editStoreConnectionJson: connectionJson,
            loggerFactory: agentServices.LoggerFactory);
        return Task.FromResult(toolset);
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

    public static IToolsetFactory Combine(params IToolsetFactory[] factories)
    {
        ArgumentNullException.ThrowIfNull(factories);

        var combinedMap = new Dictionary<string, Func<Dictionary<string, object>, AgentServices, Task<IToolset>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var factory in factories)
        {
            if (factory is null)
            {
                continue;
            }

            if (factory is ToolsetFactory concreteFactory)
            {
                foreach (var (name, createToolset) in concreteFactory.createToolsetByName)
                {
                    combinedMap[name] = createToolset;
                }
            }
            else
            {
                throw new InvalidOperationException(
                    $"Cannot combine factory type '{factory.GetType().Name}'. Only {nameof(ToolsetFactory)} instances are supported.");
            }
        }

        return new ToolsetFactory(combinedMap);
    }

    private sealed class FixedToolset : IToolset
    {
        private readonly IList<AITool> tools;

        public FixedToolset(IList<AITool> tools)
        {
            this.tools = tools ?? throw new ArgumentNullException(nameof(tools));
        }

        public Task<IList<AITool>> ListToolsAsync()
        {
            return Task.FromResult(this.tools);
        }
    }

    private sealed class EmptyToolsetFactory : IToolsetFactory
    {
        public static readonly EmptyToolsetFactory Instance = new();

        private EmptyToolsetFactory()
        {
        }

        public Task<IToolset> CreateToolsetAsync(
            string name,
            Dictionary<string, object> properties,
            AgentServices agentServices)
        {
            _ = properties;
            _ = agentServices;
            throw new InvalidOperationException($"No underlying toolset factory is configured for '{name}'.");
        }
    }
}

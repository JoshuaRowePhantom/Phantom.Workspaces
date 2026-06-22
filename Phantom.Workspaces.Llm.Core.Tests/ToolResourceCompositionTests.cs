using System.Collections.ObjectModel;
using AgentSchema;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class ToolResourceCompositionTests
{
    private sealed class MutableToolResourceRepository : IToolResourceRepository
    {
        private readonly ObservableCollection<ToolResource> resources = [];

        public MutableToolResourceRepository()
        {
            this.ToolResources = new ReadOnlyObservableCollection<ToolResource>(this.resources);
        }

        public ReadOnlyObservableCollection<ToolResource> ToolResources { get; }

        public void Add(ToolResource toolResource) => this.resources.Add(toolResource);

        public void Remove(ToolResource toolResource) => this.resources.Remove(toolResource);
    }

    private sealed class NamedToolResourceFactory : IToolResourceFactory
    {
        private readonly string id;

        public NamedToolResourceFactory(string id)
        {
            this.id = id;
        }

        public Task<Tool?> ResolveToolResourceAsync(
            ToolResource toolResource,
            CancellationToken cancellationToken = default)
        {
            if (!string.Equals(toolResource.Id, this.id, StringComparison.Ordinal))
            {
                return Task.FromResult<Tool?>(null);
            }

            return Task.FromResult<Tool?>(new CustomTool { Kind = this.id, Name = toolResource.Name });
        }
    }

    private static ToolResource ToolResource(string id, string name) => new()
    {
        Kind = "tool",
        Id = id,
        Name = name,
    };

    [Fact]
    public void FixedToolResourceRepository_ExposesDefaultBuiltInToolsets()
    {
        var repository = new FixedToolResourceRepository();

        var names = repository.ToolResources.Select(static resource => resource.Name).ToArray();
        Assert.Equal(FixedToolResources.DefaultNames, names);
        Assert.All(
            repository.ToolResources,
            static resource => Assert.Equal(FixedToolResources.FixedToolResourceId, resource.Id));
    }

    [Fact]
    public async Task FixedToolResourceFactory_ResolvesWorkspaceEntityToCustomTool()
    {
        var factory = new FixedToolResourceFactory();

        var tool = await factory.ResolveToolResourceAsync(
            ToolResource(FixedToolResources.FixedToolResourceId, FixedToolResources.WorkspaceEntity));

        var customTool = Assert.IsType<CustomTool>(tool);
        Assert.Equal(FixedToolResources.WorkspaceEntity, customTool.Kind);
        Assert.Equal(FixedToolResources.WorkspaceEntity, customTool.Name);
    }

    [Fact]
    public async Task FixedToolResourceFactory_ReturnsNullForUnknownResource()
    {
        var factory = new FixedToolResourceFactory();

        var tool = await factory.ResolveToolResourceAsync(
            ToolResource("mcp-server-entity", "github"));

        Assert.Null(tool);
    }

    [Fact]
    public async Task ComposingToolResourceFactory_TriesFactoriesInOrder()
    {
        var factory = new ComposingToolResourceFactory(
            new NamedToolResourceFactory("a"),
            new NamedToolResourceFactory("b"));

        var resolvedB = await factory.ResolveToolResourceAsync(ToolResource("b", "second"));
        var resolvedA = await factory.ResolveToolResourceAsync(ToolResource("a", "first"));
        var unresolved = await factory.ResolveToolResourceAsync(ToolResource("c", "none"));

        Assert.Equal("b", Assert.IsType<CustomTool>(resolvedB).Kind);
        Assert.Equal("a", Assert.IsType<CustomTool>(resolvedA).Kind);
        Assert.Null(unresolved);
    }

    [Fact]
    public void ComposingToolResourceRepository_AggregatesChildren()
    {
        var first = new MutableToolResourceRepository();
        var second = new MutableToolResourceRepository();
        first.Add(ToolResource("fixed", "workspace-entity"));
        second.Add(ToolResource("mcp-server-entity", "github"));

        var composing = new ComposingToolResourceRepository(first, second);

        Assert.Equal(2, composing.ToolResources.Count);
        Assert.Contains(composing.ToolResources, static resource => resource.Name == "workspace-entity");
        Assert.Contains(composing.ToolResources, static resource => resource.Name == "github");
    }

    [Fact]
    public void ComposingToolResourceRepository_ReflectsChildChanges()
    {
        var child = new MutableToolResourceRepository();
        var composing = new ComposingToolResourceRepository(child);
        Assert.Empty(composing.ToolResources);

        var resource = ToolResource("mcp-server-entity", "github");
        child.Add(resource);
        Assert.Single(composing.ToolResources);
        Assert.Equal("github", composing.ToolResources[0].Name);

        child.Remove(resource);
        Assert.Empty(composing.ToolResources);
    }
}

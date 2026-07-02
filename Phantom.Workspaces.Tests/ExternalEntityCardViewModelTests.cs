using System;
using System.Linq;
using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class ExternalEntityCardViewModelTests
{
    [PhantomAvaloniaFact]
    public void ExternalEntityCardViewModel_SingleDefaultUrl_SuppressesKeyLabel()
    {
        var entity = new SubscribedEntityViewModel(CreateExternalEntity(
            """{ "default": "https://example.com" }"""));

        var vm = ExternalEntityCardViewModel.Create(entity);

        var url = Assert.Single(vm.Urls);
        Assert.False(url.ShowKey);
        Assert.Equal("default", url.Key);
        Assert.Equal("https://example.com", url.Url);
    }

    [PhantomAvaloniaFact]
    public void ExternalEntityCardViewModel_SingleNonDefaultUrl_ShowsKeyLabel()
    {
        var entity = new SubscribedEntityViewModel(CreateExternalEntity(
            """{ "docs": "https://example.com/docs" }"""));

        var vm = ExternalEntityCardViewModel.Create(entity);

        var url = Assert.Single(vm.Urls);
        Assert.True(url.ShowKey);
        Assert.Equal("docs", url.Key);
        Assert.Equal("https://example.com/docs", url.Url);
    }

    [PhantomAvaloniaFact]
    public void ExternalEntityCardViewModel_MultipleUrls_AllShowKeyLabels()
    {
        var entity = new SubscribedEntityViewModel(CreateExternalEntity(
            """{ "default": "https://example.com", "docs": "https://example.com/docs" }"""));

        var vm = ExternalEntityCardViewModel.Create(entity);

        Assert.Collection(vm.Urls,
            u => Assert.True(u.ShowKey),
            u => Assert.True(u.ShowKey));
    }

    [PhantomAvaloniaFact]
    public void EntityCardViewResolver_ExternalEntity_ReturnsExternalViewName()
    {
        var entity = new SubscribedEntityViewModel(CreateExternalEntity(
            """{ "default": "https://example.com" }"""));
        var resolver = new EntityCardViewResolver();

        var viewName = resolver.ResolveViewName(entity);

        Assert.Equal("external", viewName);
    }

    [PhantomAvaloniaFact]
    public void EntityCardViewResolver_NonExternalEntity_ReturnsRaw()
    {
        var snapshot = CreateSnapshot(
            """
            {
              "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
              "entity-types": ["entity", "workspace"],
              "names": [["workspaces", "my-workspace"]],
              "display-name": { "default": "My Workspace" }
            }
            """);
        var resolver = new EntityCardViewResolver();

        var viewName = resolver.ResolveViewName(new SubscribedEntityViewModel(snapshot));

        Assert.Equal(EntityCardViewResolver.RawViewName, viewName);
    }

    [PhantomAvaloniaFact]
    public void EntityCardViewResolver_ExternalEntity_WithRawRequested_ReturnsRaw()
    {
        var entity = CreateExternalEntity("""{ "default": "https://example.com" }""");
        var resolver = new EntityCardViewResolver();

        var viewName = resolver.ResolveViewName(
            new SubscribedEntityViewModel(entity),
            EntityCardViewResolver.RawViewName);

        Assert.Equal(EntityCardViewResolver.RawViewName, viewName);
    }

    private static EntitySnapshot CreateExternalEntity(string urlsJson)
    {
        var json = $$"""
            {
              "entity-id": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
              "entity-types": ["entity", "external"],
              "names": [["externals", "my-link"]],
              "display-name": { "default": "My Link" },
              "urls": {{urlsJson}}
            }
            """;
        return CreateSnapshot(json);
    }

    private static EntitySnapshot CreateSnapshot(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new EntitySnapshot
        {
            EntityId = new EntityId(document.RootElement.GetProperty("entity-id").GetString()!),
            ConcurrencyTag = new ConcurrencyTag("1"),
            ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
            Data = document.RootElement.Clone(),
            Relationships = Array.Empty<EntitySnapshot>(),
        };
    }
}

using Avalonia.Headless.XUnit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.ViewModels;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Tests;

public sealed class ExternalEntityCardViewModelTests
{
    private sealed class RecordingOpener : IUrlOpener
    {
        public List<OpenUrlRequest> Requests { get; } = new();
        public bool Fail { get; set; }

        public Task OpenAsync(OpenUrlRequest request, CancellationToken cancellationToken = default)
        {
            if (this.Fail)
            {
                throw new InvalidOperationException("Cannot launch");
            }

            this.Requests.Add(request);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingTabService : IWorkspaceTabService
    {
        public List<WorkspaceTabViewModel> OpenedTabs { get; } = new();
        public List<string> FocusAttempts { get; } = new();

        public Task OpenTabAsync(WorkspaceTabViewModel tab, string? insertAfterTabId = null,
            bool focus = true, string? workspacePaneId = null)
        {
            this.OpenedTabs.Add(tab);
            return Task.CompletedTask;
        }

        public Task<bool> TryFocusExistingWebTabAsync(string url)
        {
            this.FocusAttempts.Add(url);
            return Task.FromResult(this.OpenedTabs.OfType<WebViewModel>().Any(tab => tab.AddressBarUrl == url));
        }

        public Task ReplaceTabAsync(WorkspaceTabViewModel oldTab, WorkspaceTabViewModel newTab) => Task.CompletedTask;
        public void CloseTab(WorkspaceTabViewModel tab) { }
    }

    private static async Task ClickAsync(ExternalUrlViewModel url)
    {
        url.OpenCommand.Execute(null);
        await url.OpenCommand.LastExecutionTask!;
    }

    [AvaloniaFact]
    public async Task ExternalEntityCardViewModel_DefaultHttpUrl_OpensEmbeddedWebTab()
    {
        var tabs = new RecordingTabService();
        var external = new List<string>();
        var opener = new UrlOpener(tabs, url => { external.Add(url); return Task.CompletedTask; });
        var card = ExternalEntityCardViewModel.Create(
            new SubscribedEntityViewModel(CreateExternalEntity("""{ "default": "http://example.com" }""")),
            () => opener);

        var link = Assert.Single(card.Urls);
        Assert.False(link.ShowKey);
        await ClickAsync(link);

        Assert.Single(tabs.OpenedTabs);
        Assert.IsType<WebViewModel>(tabs.OpenedTabs[0]);
        Assert.Empty(external);
    }

    [AvaloniaFact]
    public async Task ExternalEntityCardViewModel_OpenerRegisteredAfterCardCreation_OpensUrl()
    {
        IUrlOpener? registeredOpener = null;
        var link = Assert.Single(ExternalEntityCardViewModel.Create(
            new SubscribedEntityViewModel(CreateExternalEntity("""{ "default": "https://example.com" }""")),
            () => registeredOpener).Urls);
        var opener = new RecordingOpener();
        registeredOpener = opener;

        await ClickAsync(link);

        Assert.Equal("https://example.com", Assert.Single(opener.Requests).Url);
    }

    [AvaloniaFact]
    public async Task ExternalEntityCardViewModel_NamedHttpsUrls_OpenEmbeddedWebTabs()
    {
        var opener = new RecordingOpener();
        var card = ExternalEntityCardViewModel.Create(
            new SubscribedEntityViewModel(CreateExternalEntity(
                """{ "default": "https://example.com", "docs": "https://example.com/docs" }""")),
            () => opener);

        foreach (var link in card.Urls)
        {
            Assert.True(link.ShowKey);
            await ClickAsync(link);
        }

        Assert.Equal(new[] { "https://example.com", "https://example.com/docs" },
            opener.Requests.Select(request => request.Url));
        Assert.All(opener.Requests, request => Assert.Equal(UrlOpenPreference.Auto, request.Preference));
    }

    [AvaloniaFact]
    public async Task ExternalEntityCardViewModel_SameUrlAlreadyOpen_FocusesExistingWebTab()
    {
        var tabs = new RecordingTabService();
        var external = new List<string>();
        var opener = new UrlOpener(tabs, url => { external.Add(url); return Task.CompletedTask; });
        var link = Assert.Single(ExternalEntityCardViewModel.Create(
            new SubscribedEntityViewModel(CreateExternalEntity("""{ "default": "https://example.com" }""")),
            () => opener).Urls);

        await ClickAsync(link);
        await ClickAsync(link);

        Assert.Equal(2, tabs.FocusAttempts.Count);
        Assert.Single(tabs.OpenedTabs);
        Assert.Empty(external);
    }

    [AvaloniaFact]
    public async Task ExternalEntityCardViewModel_MailtoAndTelUrls_UseExternalLauncher()
    {
        var tabs = new RecordingTabService();
        var external = new List<string>();
        var opener = new UrlOpener(tabs, url => { external.Add(url); return Task.CompletedTask; });
        var card = ExternalEntityCardViewModel.Create(
            new SubscribedEntityViewModel(CreateExternalEntity(
                """{ "email": "mailto:someone@example.com", "phone": "tel:+15551234567" }""")),
            () => opener);

        foreach (var link in card.Urls)
        {
            await ClickAsync(link);
        }

        Assert.Equal(new[] { "mailto:someone@example.com", "tel:+15551234567" }, external);
        Assert.Empty(tabs.OpenedTabs);
    }

    [AvaloniaFact]
    public async Task ExternalEntityCardViewModel_InvalidOrUnsafeUrl_DoesNotLaunch()
    {
        var opener = new RecordingOpener();
        var card = ExternalEntityCardViewModel.Create(
            new SubscribedEntityViewModel(CreateExternalEntity(
                """{ "empty": "", "bad": "not a url", "script": "javascript:alert(1)", "file": "file:///secret", "credentials": "https://user:pass@example.com" }""")),
            () => opener);

        Assert.Equal(5, card.Urls.Count);
        foreach (var link in card.Urls)
        {
            Assert.False(link.OpenCommand.CanExecute(null));
            Assert.True(link.HasError);
            Assert.NotNull(link.ErrorMessage);
        }

        Assert.Empty(opener.Requests);

        var missing = Assert.Single(ExternalEntityCardViewModel.Create(
            new SubscribedEntityViewModel(CreateExternalEntity("""{ "default": "https://example.com" }"""))).Urls);
        await ClickAsync(missing);
        Assert.Contains("unavailable", missing.ErrorMessage);

        opener.Fail = true;
        var failing = Assert.Single(ExternalEntityCardViewModel.Create(
            new SubscribedEntityViewModel(CreateExternalEntity("""{ "default": "https://example.com" }""")),
            () => opener).Urls);
        await ClickAsync(failing);
        Assert.Contains("Cannot launch", failing.ErrorMessage);
    }

    [AvaloniaFact]
    public async Task EntityCardViewModel_ExternalSnapshotUpdated_UpdatedUrlsUseUrlOpener()
    {
        var entity = new SubscribedEntityViewModel(CreateExternalEntity(
            """{ "default": "https://old.example.com" }"""));
        var opener = new RecordingOpener();
        var card = new EntityCardViewModel(entity, cardViewName: "external", urlOpenerProvider: () => opener);

        Assert.False(Assert.Single(card.ExternalCard!.Urls).ShowKey);
        entity.UpdateSnapshot(CreateExternalEntity(
            """{ "default": "https://new.example.com", "docs": "https://new.example.com/docs" }"""));
        var updated = card.ExternalCard!;
        Assert.All(updated.Urls, link => Assert.True(link.ShowKey));
        foreach (var link in updated.Urls)
        {
            await ClickAsync(link);
        }

        Assert.Equal(new[] { "https://new.example.com", "https://new.example.com/docs" },
            opener.Requests.Select(request => request.Url));
    }

    [AvaloniaFact]
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

    [AvaloniaFact]
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

    [AvaloniaFact]
    public void ExternalEntityCardViewModel_MultipleUrls_AllShowKeyLabels()
    {
        var entity = new SubscribedEntityViewModel(CreateExternalEntity(
            """{ "default": "https://example.com", "docs": "https://example.com/docs" }"""));

        var vm = ExternalEntityCardViewModel.Create(entity);

        Assert.Collection(vm.Urls,
            u => Assert.True(u.ShowKey),
            u => Assert.True(u.ShowKey));
    }

    [AvaloniaFact]
    public void EntityCardViewResolver_ExternalEntity_ReturnsExternalViewName()
    {
        var entity = new SubscribedEntityViewModel(CreateExternalEntity(
            """{ "default": "https://example.com" }"""));
        var resolver = new EntityCardViewResolver();

        var viewName = resolver.ResolveViewName(entity);

        Assert.Equal("external", viewName);
    }

    [AvaloniaFact]
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

    [AvaloniaFact]
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

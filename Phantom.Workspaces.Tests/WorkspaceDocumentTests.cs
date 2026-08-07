using System;
using System.ComponentModel;
using System.Text.Json;
using System.Threading.Tasks;
using AgentSchema;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;
using Xunit;

namespace Phantom.Workspaces.Tests;

/// <summary>
/// #1198: recursive DisposeAsync from a workspace-pane document down through its
/// child WorkspaceDocument(s) to their WorkspaceTabViewModel(s), so that closing a
/// workspace pane always releases per-tab resources (notably the
/// AgentSessionWorkspaceTabViewModel's RunningAgentChatLease).
/// </summary>
public sealed class WorkspaceDocumentTests
{
    [Fact]
    public async Task WorkspaceDocument_WhenDisposed_DisposesTabViewModel()
    {
        var tab = new DisposeSpyTab("t1");
        var doc = new WorkspaceDocument(tab);

        await doc.DisposeAsync();

        Assert.Equal(1, tab.DisposeCount);
    }

    [Fact]
    public async Task WorkspaceDocument_WhenDisposed_UnsubscribesPropertyChangedHandlers()
    {
        var tab = new DisposeSpyTab("t2");
        var doc = new WorkspaceDocument(tab);
        var originalTitle = doc.Title;

        await doc.DisposeAsync();

        // After disposal, subsequent property changes on the tab must not mutate
        // the document header — the subscription has been torn down.
        tab.RaiseTitleChanged("changed-after-dispose");

        Assert.Equal(originalTitle, doc.Title);
    }

    [Fact]
    public async Task WorkspacePaneDocument_WhenDisposed_DisposesWorkspacePane()
    {
        var pane = new WorkspacePaneViewModel(CreateWorkspaceEntity());
        var tab = new DisposeSpyTab("child-tab");
        pane.Tabs.Add(tab);

        var doc = new WorkspacePaneDocument(pane);
        await doc.DisposeAsync();

        // Pane's recursive DisposeAsync must have run — the child tab was disposed.
        Assert.Equal(1, tab.DisposeCount);
        Assert.Empty(pane.Tabs);
    }

    [Fact]
    public async Task WorkspaceDocument_DisposeAsync_IsIdempotent()
    {
        var tab = new DisposeSpyTab("t3");
        var doc = new WorkspaceDocument(tab);

        await doc.DisposeAsync();
        await doc.DisposeAsync();

        Assert.Equal(1, tab.DisposeCount);
    }

    [Fact]
    public void WorkspaceDocument_InitializeWithLongTitle_KeepsFullTitleInEffectiveTabHeader()
    {
        const string longTitle = "GitHub Copilot Workspace Assistant session";
        var tab = new DisposeSpyTab("long-title") { Title = longTitle };

        var doc = new WorkspaceDocument(tab);

        Assert.Equal(longTitle, doc.Title);
        Assert.Equal(longTitle, doc.EffectiveTabHeader.Title);
        Assert.False(doc.EffectiveTabHeader.Title.EndsWith("...", StringComparison.Ordinal));
    }

    [Fact]
    public void WorkspaceTabViewModel_SetTitleExplicit_MarksIsTitleExplicitTrue()
    {
        var tab = new DisposeSpyTab("explicit-title");

        tab.SetTitleExplicit("User Title");

        Assert.True(tab.IsTitleExplicit);
        Assert.Equal("User Title", tab.Title);
    }

    [Fact]
    public void WorkspaceTabViewModel_SetTitleFromContent_IsIgnoredWhenIsTitleExplicit()
    {
        var tab = new DisposeSpyTab("explicit-content");
        tab.SetTitleExplicit("User Title");

        tab.SetTitleFromContent("Page Title");

        Assert.True(tab.IsTitleExplicit);
        Assert.Equal("User Title", tab.Title);
    }

    [Fact]
    public void WorkspaceTabViewModel_SetTitleFromContent_UpdatesTitleWhenNotExplicit()
    {
        var tab = new DisposeSpyTab("content-title");

        tab.SetTitleFromContent("Page Title");

        Assert.False(tab.IsTitleExplicit);
        Assert.Equal("Page Title", tab.Title);
    }

    private static SubscribedEntityViewModel CreateWorkspaceEntity()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "entity-id": "22222222-2222-2222-2222-222222222222",
              "entity-types": ["entity", "workspace"],
              "display-name": { "default": "Workspace" }
            }
            """);
        return new SubscribedEntityViewModel(
            new EntitySnapshot
            {
                EntityId = new EntityId("22222222-2222-2222-2222-222222222222"),
                ConcurrencyTag = new ConcurrencyTag("1"),
                ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
                Data = document.RootElement.Clone(),
                Relationships = Array.Empty<EntitySnapshot>(),
            });
    }

    private sealed class DisposeSpyTab : WorkspaceTabViewModel
    {
        public int DisposeCount { get; private set; }

        [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
        public DisposeSpyTab(string id)
        {
            this.Id = id;
            this.Title = id;
            this.DockRegion = "full";
        }

        public void RaiseTitleChanged(string newTitle) => this.Title = newTitle;

        public override async ValueTask DisposeAsync()
        {
            this.DisposeCount++;
            await base.DisposeAsync();
        }
    }
}

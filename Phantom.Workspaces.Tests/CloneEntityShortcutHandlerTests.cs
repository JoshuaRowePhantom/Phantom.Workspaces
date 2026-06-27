using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Dock.Model.Controls;
using Dock.Model.Core;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class CloneEntityShortcutHandlerTests
{
    [AvaloniaFact]
    public void ShouldApplyTo_ReturnsFalse_WhenShortcutIsNotClone()
    {
        var handler = new CloneEntityShortcutHandler();
        var mainWindowViewModel = new MainWindowViewModel(new UnknownRepositorySource());
        var entity = CreateCloneableEntity();

        Assert.False(handler.ShouldApplyTo(mainWindowViewModel, Shortcut.Open, entity));
        Assert.False(handler.ShouldApplyTo(mainWindowViewModel, Shortcut.Delete, entity));
        Assert.False(handler.ShouldApplyTo(mainWindowViewModel, Shortcut.Edit, entity));
    }

    [AvaloniaFact]
    public void ShouldApplyTo_ReturnsFalse_WhenEntityCannotBeEdited()
    {
        var handler = new CloneEntityShortcutHandler();
        var mainWindowViewModel = new MainWindowViewModel(new UnknownRepositorySource());
        var entity = CreateNonEditableEntity();

        Assert.False(handler.ShouldApplyTo(mainWindowViewModel, Shortcut.Clone, entity));
    }

    [AvaloniaFact]
    public void ShouldApplyTo_ReturnsTrue_WhenCloneShortcutAndCanEditEntity()
    {
        var handler = new CloneEntityShortcutHandler();
        var mainWindowViewModel = new MainWindowViewModel(new UnknownRepositorySource());
        var entity = CreateCloneableEntity();

        Assert.True(handler.ShouldApplyTo(mainWindowViewModel, Shortcut.Clone, entity));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Handle_OpensCloneEntityWorkspaceTabViewModel_WithoutCreatingEntity()
    {
        var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var handler = new CloneEntityShortcutHandler();
        var entity = CreateCloneableEntity();

        await handler.Handle(viewModel, Shortcut.Clone, entity);

        var documentDock = GetDocumentDock(viewModel);
        var cloneTab = documentDock?.VisibleDockables
            ?.OfType<WorkspaceDocument>()
            .Select(d => d.TabViewModel)
            .OfType<CloneEntityWorkspaceTabViewModel>()
            .FirstOrDefault();

        Assert.NotNull(cloneTab);
        Assert.Same(entity, cloneTab.Entity);
        Assert.NotNull(cloneTab.Editor);
    }

    [Fact]
    public void RewriteRelationshipParticipantIds_ReplacesSourceIdInParticipants()
    {
        var sourceId = new EntityId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var cloneId = new EntityId(Guid.Parse("22222222-2222-2222-2222-222222222222"));

        using var doc = JsonDocument.Parse("""
            {
                "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "entity-types": ["entity", "relationship"],
                "participants": {
                    "source": "11111111-1111-1111-1111-111111111111",
                    "target": "33333333-3333-3333-3333-333333333333"
                }
            }
            """);

        var result = CloneEntityEditorViewModel.RewriteRelationshipParticipantIds(
            doc.RootElement,
            sourceId,
            cloneId);

        result.TryGetProperty("participants", out var participants);
        participants.TryGetProperty("source", out var source);
        participants.TryGetProperty("target", out var target);

        Assert.Equal("22222222-2222-2222-2222-222222222222", source.GetString());
        Assert.Equal("33333333-3333-3333-3333-333333333333", target.GetString());
    }

    [Fact]
    public void RewriteRelationshipParticipantIds_DoesNotRewriteEntityIdOutsideParticipants()
    {
        var sourceId = new EntityId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var cloneId = new EntityId(Guid.Parse("22222222-2222-2222-2222-222222222222"));

        using var doc = JsonDocument.Parse("""
            {
                "entity-id": "11111111-1111-1111-1111-111111111111",
                "participants": {
                    "source": "11111111-1111-1111-1111-111111111111"
                }
            }
            """);

        var result = CloneEntityEditorViewModel.RewriteRelationshipParticipantIds(
            doc.RootElement,
            sourceId,
            cloneId);

        result.TryGetProperty("entity-id", out var entityId);
        result.TryGetProperty("participants", out var participants);
        participants.TryGetProperty("source", out var source);

        Assert.Equal("11111111-1111-1111-1111-111111111111", entityId.GetString());
        Assert.Equal("22222222-2222-2222-2222-222222222222", source.GetString());
    }

    [Fact]
    public void RewriteRelationshipParticipantIds_HandlesNestedParticipantsStructure()
    {
        var sourceId = new EntityId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var cloneId = new EntityId(Guid.Parse("22222222-2222-2222-2222-222222222222"));

        using var doc = JsonDocument.Parse("""
            {
                "participants": {
                    "roles": {
                        "owner": "11111111-1111-1111-1111-111111111111",
                        "member": "44444444-4444-4444-4444-444444444444"
                    }
                }
            }
            """);

        var result = CloneEntityEditorViewModel.RewriteRelationshipParticipantIds(
            doc.RootElement,
            sourceId,
            cloneId);

        result.TryGetProperty("participants", out var participants);
        participants.TryGetProperty("roles", out var roles);
        roles.TryGetProperty("owner", out var owner);
        roles.TryGetProperty("member", out var member);

        Assert.Equal("22222222-2222-2222-2222-222222222222", owner.GetString());
        Assert.Equal("44444444-4444-4444-4444-444444444444", member.GetString());
    }

    private static SubscribedEntityViewModel CreateCloneableEntity()
    {
        using var document = JsonDocument.Parse("""
            {
                "entity-id": "99999999-9999-9999-9999-999999999999",
                "entity-types": ["entity", "note"],
                "names": [["tests", "note"]],
                "display-name": { "default": "Test Note" }
            }
            """);

        return new SubscribedEntityViewModel(
            new EntitySnapshot
            {
                EntityId = new EntityId("99999999-9999-9999-9999-999999999999"),
                ConcurrencyTag = new ConcurrencyTag("1"),
                ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
                Data = document.RootElement.Clone(),
                Relationships = Array.Empty<EntitySnapshot>(),
            },
            saveEntityAsync: (_, _) => Task.CompletedTask);
    }

    private static SubscribedEntityViewModel CreateNonEditableEntity()
    {
        using var document = JsonDocument.Parse("""
            {
                "entity-id": "99999999-9999-9999-9999-999999999999",
                "entity-types": ["entity", "note"],
                "names": [["tests", "note"]],
                "display-name": { "default": "Test Note" }
            }
            """);

        return new SubscribedEntityViewModel(
            new EntitySnapshot
            {
                EntityId = new EntityId("99999999-9999-9999-9999-999999999999"),
                ConcurrencyTag = new ConcurrencyTag("1"),
                ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
                Data = document.RootElement.Clone(),
                Relationships = Array.Empty<EntitySnapshot>(),
            });
    }

    private static IDocumentDock? GetDocumentDock(MainWindowViewModel viewModel)
    {
        var contentLayout = viewModel.SelectedWorkspacePane?.ContentLayout;
        if (contentLayout is null)
        {
            return null;
        }

        return FindDocumentDockIn(contentLayout);
    }

    private static IDocumentDock? FindDocumentDockIn(IDockable dockable)
    {
        if (dockable is IDocumentDock documentDock)
        {
            return documentDock;
        }

        if (dockable is IDock dock && dock.VisibleDockables is not null)
        {
            foreach (var child in dock.VisibleDockables)
            {
                var result = FindDocumentDockIn(child);
                if (result is not null)
                {
                    return result;
                }
            }
        }

        return null;
    }
}

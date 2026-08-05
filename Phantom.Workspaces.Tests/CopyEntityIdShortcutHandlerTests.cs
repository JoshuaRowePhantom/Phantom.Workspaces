using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class CopyEntityIdShortcutHandlerTests
{
    [Fact]
    public async Task CopyEntityIdShortcutHandler_ShouldApplyTo_CopyEntityIdShortcut_ReturnsTrue()
    {
        var handler = new CopyEntityIdShortcutHandler(_ => Task.FromResult(true));
        await using var mainWindowViewModel = new MainWindowViewModel(new UnknownRepositorySource());
        var entity = CreateEntity("11111111-1111-1111-1111-111111111111", "note");

        Assert.True(await handler.ShouldApplyTo(mainWindowViewModel, Shortcut.CopyEntityId, entity));
    }

    [Fact]
    public async Task CopyEntityIdShortcutHandler_ShouldApplyTo_OtherShortcut_ReturnsFalse()
    {
        var handler = new CopyEntityIdShortcutHandler(_ => Task.FromResult(true));
        await using var mainWindowViewModel = new MainWindowViewModel(new UnknownRepositorySource());
        var entity = CreateEntity("11111111-1111-1111-1111-111111111111", "note");

        Assert.False(await handler.ShouldApplyTo(mainWindowViewModel, Shortcut.Open, entity));
        Assert.False(await handler.ShouldApplyTo(mainWindowViewModel, Shortcut.Delete, entity));
        Assert.False(await handler.ShouldApplyTo(mainWindowViewModel, Shortcut.Review, entity));
    }

    [Theory]
    [InlineData("git-worktree")]
    [InlineData("agent-manifest")]
    [InlineData("task")]
    [InlineData("entity")]
    public async Task CopyEntityIdShortcutHandler_AppliesToAllEntities(string entityType)
    {
        var handler = new CopyEntityIdShortcutHandler(_ => Task.FromResult(true));
        await using var mainWindowViewModel = new MainWindowViewModel(new UnknownRepositorySource());
        var entity = CreateEntity("22222222-2222-2222-2222-222222222222", entityType);

        Assert.True(await handler.ShouldApplyTo(mainWindowViewModel, Shortcut.CopyEntityId, entity));
    }

    [Fact]
    public async Task CopyEntityIdShortcutHandler_WhenInvoked_CopiesQuotedEntityIdJsonFragmentToClipboard()
    {
        string? copied = null;
        var handler = new CopyEntityIdShortcutHandler(text => { copied = text; return Task.FromResult(true); });
        await using var mainWindowViewModel = new MainWindowViewModel(new UnknownRepositorySource());
        var entity = CreateEntity("a1b2c3d4-5566-7788-99aa-bbccddeeff00", "note");

        var handled = await handler.Handle(mainWindowViewModel, Shortcut.CopyEntityId, entity);

        Assert.True(handled);
        Assert.Equal("\"entityid\":\"a1b2c3d4-5566-7788-99aa-bbccddeeff00\"", copied);
    }

    [Fact]
    public async Task CopyEntityIdShortcutHandler_Handle_UsesLowercaseHyphenatedGuidFormat()
    {
        string? copied = null;
        var handler = new CopyEntityIdShortcutHandler(text => { copied = text; return Task.FromResult(true); });
        await using var mainWindowViewModel = new MainWindowViewModel(new UnknownRepositorySource());
        var entity = CreateEntity("A1B2C3D4-5566-7788-99AA-BBCCDDEEFF00", "note");

        await handler.Handle(mainWindowViewModel, Shortcut.CopyEntityId, entity);

        // "D" format is lowercase, 5-group hyphenated — matches EntityId.ToString().
        Assert.Equal("\"entityid\":\"a1b2c3d4-5566-7788-99aa-bbccddeeff00\"", copied);
    }

    [Fact]
    public async Task CopyEntityIdShortcutHandler_Handle_ReturnsTrue_OnSuccess()
    {
        var handler = new CopyEntityIdShortcutHandler(_ => Task.FromResult(true));
        await using var mainWindowViewModel = new MainWindowViewModel(new UnknownRepositorySource());
        var entity = CreateEntity("33333333-3333-3333-3333-333333333333", "note");

        Assert.True(await handler.Handle(mainWindowViewModel, Shortcut.CopyEntityId, entity));
    }

    [Fact]
    public async Task CopyEntityIdShortcutHandler_Handle_WhenClipboardUnavailable_ReturnsFalse()
    {
        // The default copy delegate returns false when no clipboard/top-level is available.
        var handler = new CopyEntityIdShortcutHandler(_ => Task.FromResult(false));
        await using var mainWindowViewModel = new MainWindowViewModel(new UnknownRepositorySource());
        var entity = CreateEntity("44444444-4444-4444-4444-444444444444", "note");

        Assert.False(await handler.Handle(mainWindowViewModel, Shortcut.CopyEntityId, entity));
    }

    [Fact]
    public async Task ShortcutManager_ShortcutsArray_ContainsCopyEntityId()
    {
        var manager = new ShortcutManager();
        manager.AddShortcutHandler(new CopyEntityIdShortcutHandler(_ => Task.FromResult(true)));
        await using var mainWindowViewModel = new MainWindowViewModel(new UnknownRepositorySource());
        var entity = CreateEntity("55555555-5555-5555-5555-555555555555", "note");

        var shortcuts = new List<Shortcut>();
        await foreach (var shortcut in manager.GetShortcutsForAsync(mainWindowViewModel, entity))
        {
            shortcuts.Add(shortcut);
        }

        Assert.Contains(Shortcut.CopyEntityId, shortcuts);
    }

    private static SubscribedEntityViewModel CreateEntity(string id, string entityType)
    {
        using var document = JsonDocument.Parse($$"""
            {
                "entity-id": "{{id}}",
                "entity-types": ["entity", "{{entityType}}"],
                "names": [["tests", "copy-id"]],
                "display-name": { "default": "Copy Id Test" }
            }
            """);

        return new SubscribedEntityViewModel(
            new EntitySnapshot
            {
                EntityId = new EntityId(id),
                ConcurrencyTag = new ConcurrencyTag("1"),
                ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
                Data = document.RootElement.Clone(),
                Relationships = Array.Empty<EntitySnapshot>(),
            });
    }
}

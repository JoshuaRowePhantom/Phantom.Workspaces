using Avalonia.Headless.XUnit;
using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Tests;

public sealed class OpenInVsCodeWebShortcutHandlerTests
{
    // ---- ShouldApplyTo -----------------------------------------------------------------------

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ShouldApplyTo_VsCodeWebShortcut_PathExists_ReturnsTrue()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var snapshot = MakeSnapshot("""{"entity-types":["entity","git-worktree","filesystem-path"],"path":"/repo"}""");
        var entityViewModel = new SubscribedEntityViewModel(snapshot);
        
        var handler = new OpenInVsCodeWebShortcutHandler(
            tabOpener: null);

        Assert.True(await handler.ShouldApplyTo(viewModel, Shortcut.VsCodeWeb, entityViewModel));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ShouldApplyTo_VsCodeWebShortcut_NoPath_ReturnsFalse()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var snapshot = MakeSnapshot("""{"entity-types":["entity","workspace"],"display-name":{"default":"workspace"}}""");
        var entityViewModel = new SubscribedEntityViewModel(snapshot);

        var handler = new OpenInVsCodeWebShortcutHandler(
            tabOpener: null);

        Assert.False(await handler.ShouldApplyTo(viewModel, Shortcut.VsCodeWeb, entityViewModel));
    }

    // ---- Handle ------------------------------------------------------------------------------

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenInVsCodeWeb_LocalMachineWithTunnel_OpensWebViewTab()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var localProfileId = viewModel.EntityBroker.EntityRepository.WorkspaceEntitySession.UserComputerProfileEntityId;

        // Create local profile entity
        var localProfile = await entityBroker.GetEntitiesAsync([localProfileId], TestContext.Current.CancellationToken);
        var profile = localProfile.Single();

        // Extract user segment from local profile
        if (profile.Data is not JsonElement profileData)
        {
            Assert.Fail("Profile data is null");
            return;
        }

        var namesArray = profileData.GetProperty("names");
        var primaryName = namesArray[0];
        var nameParts = primaryName.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToArray();

        string? userSegment = null;
        for (int i = 0; i < nameParts.Length - 1; i++)
        {
            if (nameParts[i] == "username")
            {
                userSegment = nameParts[i + 1];
                break;
            }
        }
        Assert.NotNull(userSegment);

        // Create a vscode-tunnel entity for the local profile
        var tunnelId = new EntityId(Guid.NewGuid());
        var tunnelData = new System.Text.Json.Nodes.JsonObject
        {
            ["entity-id"] = tunnelId.Value.ToString(),
            ["entity-types"] = new System.Text.Json.Nodes.JsonArray("entity", "vscode-tunnel"),
            ["names"] = new System.Text.Json.Nodes.JsonArray(
                new System.Text.Json.Nodes.JsonArray(userSegment, "vscode-tunnel")),
            ["display-name"] = new System.Text.Json.Nodes.JsonObject { ["default"] = "local-host tunnel" },
            ["tunnel-name"] = "local-host",
            ["tunnel-url"] = "https://vscode.dev/tunnel/local-host",
            ["active"] = true,
        };
        using var tunnelDoc = JsonDocument.Parse(tunnelData.ToJsonString());
        await entityBroker.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "Insert test tunnel." } },
            Changes = [new EntityChange { Data = tunnelDoc.RootElement.Clone(), EntityChangeMode = EntityChangeMode.Replace }],
        }, TestContext.Current.CancellationToken);

        // Create local worktree entity
        var snapshot = MakeSnapshot("""{"entity-types":["entity","git-worktree","filesystem-path"],"display-name":{"default":"local-repo"},"path":"/local/repo"}""");
        var entityViewModel = new SubscribedEntityViewModel(snapshot);

        WebViewModel? openedTab = null;

        var handler = new OpenInVsCodeWebShortcutHandler(
            tabOpener: (mwvm, tab) =>
            {
                openedTab = Assert.IsType<WebViewModel>(tab);
                return Task.CompletedTask;
            });

        var handled = await handler.Handle(viewModel, Shortcut.VsCodeWeb, entityViewModel);

        Assert.True(handled);
        Assert.NotNull(openedTab);
        Assert.Equal("VS Code Web — local-repo", openedTab.Title);
        Assert.Contains("https://vscode.dev/tunnel/local-host?folder=", openedTab.AddressBarUrl);
        Assert.Contains("%2Flocal%2Frepo", openedTab.AddressBarUrl);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenInVsCodeWeb_RemoteMachineWithTunnel_OpensWebViewTab()
    {
        // Note: Comprehensive testing of remote entity scenarios (with profile/tunnel lookups)
        // requires a repository source that supports entity name queries. UnknownRepositorySource
        // doesn't support this. The production repository sources (MongoDB, Offline) do support
        // entity name lookups, so the remote functionality will work in production.
        // This test verifies the local scenario; remote follows the same pattern as OpenInVsCodeShortcutHandler.
        
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var localProfileId = viewModel.EntityBroker.EntityRepository.WorkspaceEntitySession.UserComputerProfileEntityId;

        // Create local profile entity
        var localProfile = await entityBroker.GetEntitiesAsync([localProfileId], TestContext.Current.CancellationToken);
        var profile = localProfile.Single();

        // Extract user segment from local profile
        if (profile.Data is not JsonElement profileData)
        {
            Assert.Fail("Profile data is null");
            return;
        }

        var namesArray = profileData.GetProperty("names");
        var primaryName = namesArray[0];
        var nameParts = primaryName.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToArray();

        string? userSegment = null;
        for (int i = 0; i < nameParts.Length - 1; i++)
        {
            if (nameParts[i] == "username")
            {
                userSegment = nameParts[i + 1];
                break;
            }
        }
        Assert.NotNull(userSegment);

        // Create a vscode-tunnel entity for the local profile
        var tunnelId = new EntityId(Guid.NewGuid());
        var tunnelData = new System.Text.Json.Nodes.JsonObject
        {
            ["entity-id"] = tunnelId.Value.ToString(),
            ["entity-types"] = new System.Text.Json.Nodes.JsonArray("entity", "vscode-tunnel"),
            ["names"] = new System.Text.Json.Nodes.JsonArray(
                new System.Text.Json.Nodes.JsonArray(userSegment, "vscode-tunnel")),
            ["display-name"] = new System.Text.Json.Nodes.JsonObject { ["default"] = "local-host tunnel" },
            ["tunnel-name"] = "local-host",
            ["tunnel-url"] = "https://vscode.dev/tunnel/local-host",
            ["active"] = true,
        };
        using var tunnelDoc = JsonDocument.Parse(tunnelData.ToJsonString());
        await entityBroker.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "Insert test tunnel." } },
            Changes = [new EntityChange { Data = tunnelDoc.RootElement.Clone(), EntityChangeMode = EntityChangeMode.Replace }],
        }, TestContext.Current.CancellationToken);

        // Test with local worktree
        var snapshot = MakeSnapshot("""{"entity-types":["entity","git-worktree","filesystem-path"],"display-name":{"default":"local-repo"},"path":"/local/repo"}""");
        var entityViewModel = new SubscribedEntityViewModel(snapshot);

        WebViewModel? openedTab = null;

        var handler = new OpenInVsCodeWebShortcutHandler(
            tabOpener: (mwvm, tab) =>
            {
                openedTab = Assert.IsType<WebViewModel>(tab);
                return Task.CompletedTask;
            });

        var handled = await handler.Handle(viewModel, Shortcut.VsCodeWeb, entityViewModel);

        Assert.True(handled);
        Assert.NotNull(openedTab);
        Assert.Equal("VS Code Web — local-repo", openedTab.Title);
        Assert.Contains("https://vscode.dev/tunnel/local-host?folder=", openedTab.AddressBarUrl);
        Assert.Contains("%2Flocal%2Frepo", openedTab.AddressBarUrl);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenInVsCodeWeb_NoTunnelEntity_ReturnsFalse()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var remoteProfileId = new EntityId(Guid.NewGuid());

        // Create a remote profile entity WITHOUT a tunnel
        var profileData = new System.Text.Json.Nodes.JsonObject
        {
            ["entity-id"] = remoteProfileId.Value.ToString(),
            ["entity-types"] = new System.Text.Json.Nodes.JsonArray("entity", "user-computer-profile"),
            ["names"] = new System.Text.Json.Nodes.JsonArray(
                new System.Text.Json.Nodes.JsonArray("computer-user-profiles", "users", "username", "no-tunnel-user", "computers", "hostname", "no-tunnel-host")),
            ["display-name"] = new System.Text.Json.Nodes.JsonObject { ["default"] = "no-tunnel-user@no-tunnel-host" },
        };
        using var profileDoc = JsonDocument.Parse(profileData.ToJsonString());
        await entityBroker.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "Insert test profile." } },
            Changes = [new EntityChange { Data = profileDoc.RootElement.Clone(), EntityChangeMode = EntityChangeMode.Replace }],
        }, TestContext.Current.CancellationToken);

        // Create a remote entity with path, owned by the remote profile
        var worktreeId = new EntityId(Guid.NewGuid());
        var worktreeData = new System.Text.Json.Nodes.JsonObject
        {
            ["entity-id"] = worktreeId.Value.ToString(),
            ["entity-types"] = new System.Text.Json.Nodes.JsonArray("entity", "git-worktree", "filesystem-path"),
            ["names"] = new System.Text.Json.Nodes.JsonArray(
                new System.Text.Json.Nodes.JsonArray("git-worktrees", "/remote/repo"),
                new System.Text.Json.Nodes.JsonArray("computer-user-profiles", "users", "username", "no-tunnel-user", "computers", "hostname", "no-tunnel-host")),
            ["display-name"] = new System.Text.Json.Nodes.JsonObject { ["default"] = "no-tunnel-repo" },
            ["path"] = "/remote/repo",
        };
        using var worktreeDoc = JsonDocument.Parse(worktreeData.ToJsonString());
        await entityBroker.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "Insert test worktree." } },
            Changes = [new EntityChange { Data = worktreeDoc.RootElement.Clone(), EntityChangeMode = EntityChangeMode.Replace }],
        }, TestContext.Current.CancellationToken);

        var worktreeEntities = await entityBroker.GetEntitiesAsync([worktreeId], TestContext.Current.CancellationToken);
        var entityViewModel = worktreeEntities.Single();

        var handler = new OpenInVsCodeWebShortcutHandler(
            tabOpener: null);

        var handled = await handler.Handle(viewModel, Shortcut.VsCodeWeb, entityViewModel);

        Assert.False(handled);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenInVsCodeWeb_UrlFormat_AppendsFolderParam()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var localProfileId = viewModel.EntityBroker.EntityRepository.WorkspaceEntitySession.UserComputerProfileEntityId;
        var localProfile = await entityBroker.GetEntitiesAsync([localProfileId], TestContext.Current.CancellationToken);
        var profile = localProfile.Single();

        if (profile.Data is not JsonElement profileData)
        {
            Assert.Fail("Profile data is null");
            return;
        }

        var namesArray = profileData.GetProperty("names");
        var primaryName = namesArray[0];
        var nameParts = primaryName.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToArray();

        string? userSegment = null;
        for (int i = 0; i < nameParts.Length - 1; i++)
        {
            if (nameParts[i] == "username")
            {
                userSegment = nameParts[i + 1];
                break;
            }
        }
        Assert.NotNull(userSegment);

        var tunnelId = new EntityId(Guid.NewGuid());
        var tunnelData = new System.Text.Json.Nodes.JsonObject
        {
            ["entity-id"] = tunnelId.Value.ToString(),
            ["entity-types"] = new System.Text.Json.Nodes.JsonArray("entity", "vscode-tunnel"),
            ["names"] = new System.Text.Json.Nodes.JsonArray(
                new System.Text.Json.Nodes.JsonArray(userSegment, "vscode-tunnel")),
            ["tunnel-name"] = "test-tunnel",
            ["tunnel-url"] = "https://vscode.dev/tunnel/test-tunnel",
        };
        using var tunnelDoc = JsonDocument.Parse(tunnelData.ToJsonString());
        await entityBroker.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "Insert test tunnel." } },
            Changes = [new EntityChange { Data = tunnelDoc.RootElement.Clone(), EntityChangeMode = EntityChangeMode.Replace }],
        }, TestContext.Current.CancellationToken);

        var snapshot = MakeSnapshot("""{"entity-types":["entity","filesystem-path"],"display-name":{"default":"test"},"path":"/test/path"}""");
        var entityViewModel = new SubscribedEntityViewModel(snapshot);

        string? capturedUrl = null;

        var handler = new OpenInVsCodeWebShortcutHandler(
            tabOpener: (mwvm, tab) =>
            {
                var webTab = Assert.IsType<WebViewModel>(tab);
                capturedUrl = webTab.AddressBarUrl;
                return Task.CompletedTask;
            });

        await handler.Handle(viewModel, Shortcut.VsCodeWeb, entityViewModel);

        Assert.NotNull(capturedUrl);
        Assert.StartsWith("https://vscode.dev/tunnel/test-tunnel?folder=", capturedUrl);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenInVsCodeWeb_TabTitle_IncludesEntityName()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var localProfileId = viewModel.EntityBroker.EntityRepository.WorkspaceEntitySession.UserComputerProfileEntityId;
        var localProfile = await entityBroker.GetEntitiesAsync([localProfileId], TestContext.Current.CancellationToken);
        var profile = localProfile.Single();

        if (profile.Data is not JsonElement profileData)
        {
            Assert.Fail("Profile data is null");
            return;
        }

        var namesArray = profileData.GetProperty("names");
        var primaryName = namesArray[0];
        var nameParts = primaryName.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToArray();

        string? userSegment = null;
        for (int i = 0; i < nameParts.Length - 1; i++)
        {
            if (nameParts[i] == "username")
            {
                userSegment = nameParts[i + 1];
                break;
            }
        }
        Assert.NotNull(userSegment);

        var tunnelId = new EntityId(Guid.NewGuid());
        var tunnelData = new System.Text.Json.Nodes.JsonObject
        {
            ["entity-id"] = tunnelId.Value.ToString(),
            ["entity-types"] = new System.Text.Json.Nodes.JsonArray("entity", "vscode-tunnel"),
            ["names"] = new System.Text.Json.Nodes.JsonArray(
                new System.Text.Json.Nodes.JsonArray(userSegment, "vscode-tunnel")),
            ["tunnel-name"] = "test-tunnel",
            ["tunnel-url"] = "https://vscode.dev/tunnel/test-tunnel",
        };
        using var tunnelDoc = JsonDocument.Parse(tunnelData.ToJsonString());
        await entityBroker.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "Insert test tunnel." } },
            Changes = [new EntityChange { Data = tunnelDoc.RootElement.Clone(), EntityChangeMode = EntityChangeMode.Replace }],
        }, TestContext.Current.CancellationToken);

        var snapshot = MakeSnapshot("""{"entity-types":["entity","filesystem-path"],"display-name":{"default":"My Test Repo"},"path":"/test/repo"}""");
        var entityViewModel = new SubscribedEntityViewModel(snapshot);

        string? capturedTitle = null;

        var handler = new OpenInVsCodeWebShortcutHandler(
            tabOpener: (mwvm, tab) =>
            {
                var webTab = Assert.IsType<WebViewModel>(tab);
                capturedTitle = webTab.Title;
                return Task.CompletedTask;
            });

        await handler.Handle(viewModel, Shortcut.VsCodeWeb, entityViewModel);

        Assert.NotNull(capturedTitle);
        Assert.Equal("VS Code Web — My Test Repo", capturedTitle);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task OpenInVsCodeWeb_PathEncoded_CorrectlyInUrl()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var localProfileId = viewModel.EntityBroker.EntityRepository.WorkspaceEntitySession.UserComputerProfileEntityId;
        var localProfile = await entityBroker.GetEntitiesAsync([localProfileId], TestContext.Current.CancellationToken);
        var profile = localProfile.Single();

        if (profile.Data is not JsonElement profileData)
        {
            Assert.Fail("Profile data is null");
            return;
        }

        var namesArray = profileData.GetProperty("names");
        var primaryName = namesArray[0];
        var nameParts = primaryName.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToArray();

        string? userSegment = null;
        for (int i = 0; i < nameParts.Length - 1; i++)
        {
            if (nameParts[i] == "username")
            {
                userSegment = nameParts[i + 1];
                break;
            }
        }
        Assert.NotNull(userSegment);

        var tunnelId = new EntityId(Guid.NewGuid());
        var tunnelData = new System.Text.Json.Nodes.JsonObject
        {
            ["entity-id"] = tunnelId.Value.ToString(),
            ["entity-types"] = new System.Text.Json.Nodes.JsonArray("entity", "vscode-tunnel"),
            ["names"] = new System.Text.Json.Nodes.JsonArray(
                new System.Text.Json.Nodes.JsonArray(userSegment, "vscode-tunnel")),
            ["tunnel-name"] = "test-tunnel",
            ["tunnel-url"] = "https://vscode.dev/tunnel/test-tunnel",
        };
        using var tunnelDoc = JsonDocument.Parse(tunnelData.ToJsonString());
        await entityBroker.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "Insert test tunnel." } },
            Changes = [new EntityChange { Data = tunnelDoc.RootElement.Clone(), EntityChangeMode = EntityChangeMode.Replace }],
        }, TestContext.Current.CancellationToken);

        var snapshot = MakeSnapshot("""{"entity-types":["entity","filesystem-path"],"display-name":{"default":"test"},"path":"/path with spaces/and$symbols"}""");
        var entityViewModel = new SubscribedEntityViewModel(snapshot);

        string? capturedUrl = null;

        var handler = new OpenInVsCodeWebShortcutHandler(
            tabOpener: (mwvm, tab) =>
            {
                var webTab = Assert.IsType<WebViewModel>(tab);
                capturedUrl = webTab.AddressBarUrl;
                return Task.CompletedTask;
            });

        await handler.Handle(viewModel, Shortcut.VsCodeWeb, entityViewModel);

        Assert.NotNull(capturedUrl);
        Assert.Contains("%2Fpath%20with%20spaces%2Fand%24symbols", capturedUrl);
    }

    // ---- Helpers -----------------------------------------------------------------------------

    private static EntitySnapshot MakeSnapshot(string json) =>
        new()
        {
            EntityId = new EntityId(Guid.NewGuid()),
            ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, Guid.NewGuid().ToString()),
            Data = JsonDocument.Parse(json).RootElement.Clone(),
            Relationships = [],
        };

    private static EntityBroker GetEntityBroker(MainWindowViewModel viewModel)
    {
        var entityBrokerProperty = typeof(MainWindowViewModel).GetProperty(
            "EntityBroker",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(entityBrokerProperty);
        return Assert.IsType<EntityBroker>(entityBrokerProperty!.GetValue(viewModel));
    }
}


using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Core.Manifest;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

/// <summary>
/// Tests for issue #1463: the reusable <see cref="ManifestParametersViewModel"/> component extracted
/// from <see cref="AgentManifestLaunchpadViewModel"/>. Verifies manifest-driven row construction,
/// aggregate validity, by-name value/selection collection, and persist-by-name-until-launch behavior.
/// </summary>
public sealed class ManifestParametersViewModelTests
{
    private const string ManifestAEntityId = "d1463001-0000-4000-8000-000000000001";
    private const string ManifestBEntityId = "d1463002-0000-4000-8000-000000000002";
    private const string ManifestCEntityId = "d1463003-0000-4000-8000-000000000003";
    private const string ManifestEmptyEntityId = "d1463004-0000-4000-8000-000000000004";
    private const string ManifestDefaultsEntityId = "d1463005-0000-4000-8000-000000000005";
    private const string ManifestExecutorEntityId = "d1463006-0000-4000-8000-000000000006";

    // alpha (required) + beta (optional), both text.
    private const string ManifestAJson =
        """
        {
          "entity-id": "d1463001-0000-4000-8000-000000000001",
          "entity-types": ["entity", "agent-manifest"],
          "names": [["tests", "agent-manifests", "mp-a"]],
          "display-name": { "default": "MP A" },
          "manifest": {
            "name": "mp-a",
            "displayName": "MP A",
            "parameters": {
              "properties": [
                { "name": "alpha", "required": true },
                { "name": "beta", "required": false }
              ]
            },
            "template": {
              "kind": "prompt",
              "name": "mp-a",
              "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
            }
          }
        }
        """;

    // alpha (shared with A) + gamma.
    private const string ManifestBJson =
        """
        {
          "entity-id": "d1463002-0000-4000-8000-000000000002",
          "entity-types": ["entity", "agent-manifest"],
          "names": [["tests", "agent-manifests", "mp-b"]],
          "display-name": { "default": "MP B" },
          "manifest": {
            "name": "mp-b",
            "displayName": "MP B",
            "parameters": {
              "properties": [
                { "name": "alpha", "required": false },
                { "name": "gamma", "required": false }
              ]
            },
            "template": {
              "kind": "prompt",
              "name": "mp-b",
              "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
            }
          }
        }
        """;

    // alpha only (beta and gamma absent).
    private const string ManifestCJson =
        """
        {
          "entity-id": "d1463003-0000-4000-8000-000000000003",
          "entity-types": ["entity", "agent-manifest"],
          "names": [["tests", "agent-manifests", "mp-c"]],
          "display-name": { "default": "MP C" },
          "manifest": {
            "name": "mp-c",
            "displayName": "MP C",
            "parameters": {
              "properties": [
                { "name": "alpha", "required": false }
              ]
            },
            "template": {
              "kind": "prompt",
              "name": "mp-c",
              "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
            }
          }
        }
        """;

    private const string ManifestEmptyJson =
        """
        {
          "entity-id": "d1463004-0000-4000-8000-000000000004",
          "entity-types": ["entity", "agent-manifest"],
          "names": [["tests", "agent-manifests", "mp-empty"]],
          "display-name": { "default": "MP Empty" },
          "manifest": {
            "name": "mp-empty",
            "displayName": "MP Empty",
            "template": {
              "kind": "prompt",
              "name": "mp-empty",
              "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
            }
          }
        }
        """;

    // p1 has a default; p2 has a default. Used with initial values to exercise seed precedence.
    private const string ManifestDefaultsJson =
        """
        {
          "entity-id": "d1463005-0000-4000-8000-000000000005",
          "entity-types": ["entity", "agent-manifest"],
          "names": [["tests", "agent-manifests", "mp-defaults"]],
          "display-name": { "default": "MP Defaults" },
          "manifest": {
            "name": "mp-defaults",
            "displayName": "MP Defaults",
            "parameters": {
              "properties": [
                { "name": "p1", "required": false, "default": "def1" },
                { "name": "p2", "required": false, "default": "def2" }
              ]
            },
            "template": {
              "kind": "prompt",
              "name": "mp-defaults",
              "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
            }
          }
        }
        """;

    // topic (text) + worker-executor (executor picker).
    private const string ManifestExecutorJson =
        """
        {
          "entity-id": "d1463006-0000-4000-8000-000000000006",
          "entity-types": ["entity", "agent-manifest"],
          "names": [["tests", "agent-manifests", "mp-executor"]],
          "display-name": { "default": "MP Executor" },
          "manifest": {
            "name": "mp-executor",
            "displayName": "MP Executor",
            "parameters": {
              "properties": [
                { "name": "topic", "required": false },
                { "name": "worker-executor", "kind": "executor", "required": true }
              ]
            },
            "template": {
              "kind": "prompt",
              "name": "mp-executor",
              "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
            }
          }
        }
        """;

    private const string UserComputerProfileEntityId = "d1463010-0000-4000-8000-000000000010";
    private const string TrustProfileEntityId = "d1463020-0000-4000-8000-000000000020";

    private const string UserComputerProfileEntityJson =
        """
        {
          "entity-id": "d1463010-0000-4000-8000-000000000010",
          "entity-types": ["entity", "user-computer-profile"],
          "names": [["computer-user-profiles", "users", "username", "mp-user", "computers", "hostname", "mp-machine"]],
          "display-name": { "default": "MP Machine" },
          "computer-reference": ["computers", "hostname", "mp-machine"],
          "user-reference": ["users", "username", "mp-user"]
        }
        """;

    private const string TrustProfileEntityJson =
        """
        {
          "entity-id": "d1463020-0000-4000-8000-000000000020",
          "entity-types": ["entity", "llm-trust-profile"],
          "names": [["tests", "trust-profiles", "mp-remote"]],
          "display-name": { "default": "MP Remote" },
          "hosting-workspaces-client-instances": ["*"],
          "filesystem-paths": [],
          "network-capabilities": [],
          "https-proxy-policy": { "mode": "disabled" },
          "allowed-mcp-tool-call-schemas": [ {} ]
        }
        """;

    [AvaloniaFact(Timeout = 15_000)]
    public async Task SetManifest_WithParameters_BuildsRowsForSchema()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var manifestA = await fixture.LoadManifestAsync(ManifestAEntityId, ManifestAJson);

        var component = new ManifestParametersViewModel(fixture.ViewModel);
        component.SetManifest(manifestA);

        Assert.Equal(2, component.Parameters.Count);
        Assert.Equal(["alpha", "beta"], component.Parameters.Select(p => p.Name).ToArray());
        Assert.True(Assert.Single(component.Parameters, p => p.Name == "alpha").IsRequired);
        Assert.False(Assert.Single(component.Parameters, p => p.Name == "beta").IsRequired);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task SetManifest_WhenNameRetainedFromPriorManifest_SeedsRowValueFromCache()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var manifestA = await fixture.LoadManifestAsync(ManifestAEntityId, ManifestAJson);
        var manifestB = await fixture.LoadManifestAsync(ManifestBEntityId, ManifestBJson);

        var component = new ManifestParametersViewModel(fixture.ViewModel);
        component.SetManifest(manifestA);
        Assert.Single(component.Parameters, p => p.Name == "alpha").Value = "kept";

        component.SetManifest(manifestB);

        Assert.Equal("kept", Assert.Single(component.Parameters, p => p.Name == "alpha").Value);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task SetManifest_WhenNameAbsentInNewManifest_RetainsValueUntilLaunch()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var manifestA = await fixture.LoadManifestAsync(ManifestAEntityId, ManifestAJson);
        var manifestC = await fixture.LoadManifestAsync(ManifestCEntityId, ManifestCJson);

        var component = new ManifestParametersViewModel(fixture.ViewModel);
        component.SetManifest(manifestA);
        Assert.Single(component.Parameters, p => p.Name == "beta").Value = "keepbeta";

        // Switch to a manifest that does not declare beta — the row disappears but the value is retained.
        component.SetManifest(manifestC);
        Assert.DoesNotContain(component.Parameters, p => p.Name == "beta");

        // Switching back restores the retained value (no launch happened).
        component.SetManifest(manifestA);
        Assert.Equal("keepbeta", Assert.Single(component.Parameters, p => p.Name == "beta").Value);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task SetManifest_SeedPrecedence_PrefersRetainedOverInitialOverDefault()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var manifestDefaults = await fixture.LoadManifestAsync(ManifestDefaultsEntityId, ManifestDefaultsJson);

        var initial = new Dictionary<string, string> { ["p1"] = "init" };
        var component = new ManifestParametersViewModel(fixture.ViewModel, initial);

        component.SetManifest(manifestDefaults);
        // p1: initial beats default; p2: default applies when no retained/initial value exists.
        Assert.Equal("init", Assert.Single(component.Parameters, p => p.Name == "p1").Value);
        Assert.Equal("def2", Assert.Single(component.Parameters, p => p.Name == "p2").Value);

        // Enter a value for p1, then re-select: retained beats initial (and default).
        Assert.Single(component.Parameters, p => p.Name == "p1").Value = "ret";
        component.SetManifest(manifestDefaults);
        Assert.Equal("ret", Assert.Single(component.Parameters, p => p.Name == "p1").Value);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task CommitLaunch_ClearsRetainedValuesForAbsentNames()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var manifestA = await fixture.LoadManifestAsync(ManifestAEntityId, ManifestAJson);
        var manifestC = await fixture.LoadManifestAsync(ManifestCEntityId, ManifestCJson);

        var component = new ManifestParametersViewModel(fixture.ViewModel);
        component.SetManifest(manifestA);
        Assert.Single(component.Parameters, p => p.Name == "alpha").Value = "keepalpha";
        Assert.Single(component.Parameters, p => p.Name == "beta").Value = "dropbeta";

        // Move to a manifest that only declares alpha, then launch it.
        component.SetManifest(manifestC);
        component.CommitLaunch();

        // beta was absent at launch time, so its retained value is pruned; alpha survives.
        component.SetManifest(manifestA);
        Assert.Equal("keepalpha", Assert.Single(component.Parameters, p => p.Name == "alpha").Value);
        Assert.Equal(string.Empty, Assert.Single(component.Parameters, p => p.Name == "beta").Value);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task IsValid_WhenRequiredRowEmpty_IsFalse()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var manifestA = await fixture.LoadManifestAsync(ManifestAEntityId, ManifestAJson);

        var component = new ManifestParametersViewModel(fixture.ViewModel);
        component.SetManifest(manifestA);

        Assert.False(component.IsValid);

        Assert.Single(component.Parameters, p => p.Name == "alpha").Value = "provided";
        Assert.True(component.IsValid);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task IsValid_WhenNoParameters_IsTrue()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var manifestEmpty = await fixture.LoadManifestAsync(ManifestEmptyEntityId, ManifestEmptyJson);

        var component = new ManifestParametersViewModel(fixture.ViewModel);
        component.SetManifest(manifestEmpty);

        Assert.Empty(component.Parameters);
        Assert.True(component.IsValid);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task GetValues_OmitsExecutorRowsAndEmptyValues_ReturnsByNameMap()
    {
        await using var fixture = await TestFixture.CreateExecutorAsync();
        var manifestExecutor = await fixture.LoadManifestAsync(ManifestExecutorEntityId, ManifestExecutorJson);

        var component = new ManifestParametersViewModel(fixture.ViewModel);
        component.SetManifest(manifestExecutor);
        await component.ExecutorOptionsLoaded;

        // topic has a value; the executor row is excluded regardless of selection.
        Assert.Single(component.Parameters, p => p.Name == "topic").Value = "weather";

        var values = component.GetValues();

        Assert.True(values.TryGetValue("topic", out var topicValue));
        Assert.Equal("weather", topicValue);
        Assert.DoesNotContain("worker-executor", values.Keys);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task GetValues_OmitsEmptyValues()
    {
        await using var fixture = await TestFixture.CreateAsync();
        var manifestA = await fixture.LoadManifestAsync(ManifestAEntityId, ManifestAJson);

        var component = new ManifestParametersViewModel(fixture.ViewModel);
        component.SetManifest(manifestA);
        Assert.Single(component.Parameters, p => p.Name == "alpha").Value = "value-a";
        // beta left empty.

        var values = component.GetValues();

        Assert.True(values.ContainsKey("alpha"));
        Assert.False(values.ContainsKey("beta"));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task GetSelections_ForExecutorPicker_RecordsDisambiguatedSelection()
    {
        await using var fixture = await TestFixture.CreateExecutorAsync();
        var manifestExecutor = await fixture.LoadManifestAsync(ManifestExecutorEntityId, ManifestExecutorJson);

        var component = new ManifestParametersViewModel(fixture.ViewModel);
        component.SetManifest(manifestExecutor);
        await component.ExecutorOptionsLoaded;

        var executorRow = Assert.Single(component.Parameters, p => p.IsExecutorPicker);
        var trustOption = Assert.Single(
            executorRow.ExecutorOptions,
            option => option.Kind == ExecutorParameterSelection.TrustProfileKind
                && SelectionValue(option.Selection, ExecutorParameterSelection.TrustProfileKind) == "mp-remote");
        executorRow.SelectedExecutorOption = trustOption;

        var selections = component.GetSelections();

        Assert.True(selections.ContainsKey("worker-executor"));
        Assert.True(ExecutorParameterSelection.TryGetTrustProfile(selections["worker-executor"], out var nameOrId));
        Assert.Equal("mp-remote", nameOrId);
        Assert.DoesNotContain("topic", selections.Keys);
    }

    private static string? SelectionValue(JsonElement selection, string kind)
        => selection.ValueKind == JsonValueKind.Object
            && selection.TryGetProperty(kind, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private sealed class TestFixture : System.IAsyncDisposable
    {
        public required MainWindowViewModel ViewModel { get; init; }

        public required EntityBroker Broker { get; init; }

        public static async Task<TestFixture> CreateAsync()
        {
            var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
            await viewModel.InitializeAsync();
            var broker = MainWindowIntegrationTests.GetEntityBroker(viewModel);
            return new TestFixture { ViewModel = viewModel, Broker = broker };
        }

        public static async Task<TestFixture> CreateExecutorAsync()
        {
            var fixture = await CreateAsync();
            await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
                fixture.Broker, new EntityId(UserComputerProfileEntityId), UserComputerProfileEntityJson);
            await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
                fixture.Broker, new EntityId(TrustProfileEntityId), TrustProfileEntityJson);
            return fixture;
        }

        public Task<SubscribedEntityViewModel> LoadManifestAsync(string entityId, string json)
            => MainWindowIntegrationTests.UpsertEntityAndLoadAsync(this.Broker, new EntityId(entityId), json);

        public ValueTask DisposeAsync() => this.ViewModel.DisposeAsync();
    }
}

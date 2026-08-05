using Avalonia.Headless.XUnit;
using System.Linq;
using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Tests;

public sealed class EntityReferenceSearchTests
{
    [AvaloniaFact]
    public async Task ResolveAsync_ReturnsDisplayNameAndNames_ForReferencedEntity()
    {
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            TestContext.Current.CancellationToken);
        var profileId = new EntityId("a1b2c3d4-1111-2222-3333-444455556666");
        await SeedAsync(
            broker,
            $$"""
            {
              "entity-id": "{{profileId}}",
              "entity-types": ["entity", "folder"],
              "names": [["profiles", "jrowe-daemon"]],
              "display-name": { "default": "jrowe @ DAEMON" }
            }
            """);

        var search = new EntityReferenceSearch(broker);
        var candidate = await search.ResolveAsync(profileId.ToString());

        Assert.NotNull(candidate);
        Assert.Equal("jrowe @ DAEMON", candidate!.DisplayName);
        Assert.Equal(profileId.ToString(), candidate.EntityId);
        Assert.Contains("jrowe-daemon", candidate.Names, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task ResolveAsync_ReturnsNull_ForUnknownOrInvalidId()
    {
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            TestContext.Current.CancellationToken);
        var search = new EntityReferenceSearch(broker);

        Assert.Null(await search.ResolveAsync("not-a-guid"));
        Assert.Null(await search.ResolveAsync(new EntityId("00000000-0000-0000-0000-000000000099").ToString()));
    }

    [AvaloniaFact]
    public async Task SearchAsync_FindsCandidatesByTypeAndName_CaseInsensitively()
    {
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            TestContext.Current.CancellationToken);
        await SeedAsync(
            broker,
            """
            {
              "entity-id": "11111111-1111-1111-1111-111111111111",
              "entity-types": ["entity", "schedule"],
              "names": [["schedule", "every-day-at-09"]],
              "display-name": { "default": "Every day at 09:00" }
            }
            """);

        var search = new EntityReferenceSearch(broker);
        var results = await search.SearchAsync("every DAY", ["schedule"]);

        Assert.Contains(results, candidate => candidate.DisplayName == "Every day at 09:00");
    }

    [AvaloniaFact]
    public void OpenCommand_InvokesNavigationCallbackWithReferencedId()
    {
        string? opened = null;
        var editor = new EntityReferenceFieldEditorViewModel(
            "target",
            "bc863e27-a199-f259-4001-cd1dd5b2bdb4",
            ["entity"],
            search: null,
            openEntity: id => opened = id);

        Assert.True(editor.CanOpen);
        Assert.True(editor.OpenCommand.CanExecute(null));
        editor.OpenCommand.Execute(null);

        Assert.Equal("bc863e27-a199-f259-4001-cd1dd5b2bdb4", opened);
    }

    [AvaloniaFact]
    public void OpenCommand_CannotExecute_WithoutNavigationCallback()
    {
        var editor = new EntityReferenceFieldEditorViewModel(
            "target",
            "bc863e27-a199-f259-4001-cd1dd5b2bdb4",
            ["entity"],
            search: null);

        Assert.False(editor.CanOpen);
        Assert.False(editor.OpenCommand.CanExecute(null));
    }

    private static async Task SeedAsync(
        EntityBroker broker,
        string json)
    {
        using var document = JsonDocument.Parse(json);
        await broker.EntityRepository.DataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown { Text = "Seed entity-reference search test." },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = new EntityId(document.RootElement.GetProperty("entity-id").GetString()!),
                        EntityChangeMode = EntityChangeMode.Replace,
                        Data = document.RootElement.Clone(),
                    },
                ],
            },
            TestContext.Current.CancellationToken);
    }
}

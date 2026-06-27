using System;
using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.ScheduledTools;
using Phantom.Workspaces.ViewModels;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class ScheduledToolsPauseIndicatorViewModelTests
{
    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 6, 17, 9, 30, 0, TimeSpan.Zero);
    }

    private static async Task AddHostProfileAsync(IDataAccessLayer dataAccessLayer, Guid hostId)
    {
        using var document = JsonDocument.Parse(
            $$"""{ "entity-id": "{{hostId}}", "entity-types": ["entity", "user-computer-profile"], "names": [["computer-user-profiles","users","username","test-user","computers","hostname","this-machine"]], "user-reference": ["users","username","test-user"], "computer-reference": ["computers","hostname","this-machine"] }""");
        var result = await dataAccessLayer.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "seed" } },
            Changes =
            [
                new EntityChange
                {
                    EntityId = new EntityId(hostId),
                    ConcurrencyTag = null,
                    Data = document.RootElement.Clone(),
                    EntityChangeMode = EntityChangeMode.Replace,
                },
            ],
        });
        Assert.DoesNotContain(result.EntityResults, r => r.UpdateState == UpdateState.Failed);
    }

    private static ScheduledToolPauseStateService CreateService(IDataAccessLayer dataAccessLayer)
    {
        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([]), timeProvider: new FixedTimeProvider());
        return new ScheduledToolPauseStateService(dataAccessLayer, host);
    }

    [Fact]
    public void Glyph_ReflectsNotPaused_Initially()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var hostId = new EntityId(Guid.NewGuid());
        var service = CreateService(dataAccessLayer);

        using var indicator = new ScheduledToolsPauseIndicatorViewModel(service, hostId);

        Assert.False(indicator.IsPaused);
        Assert.Equal("⏱", indicator.ButtonGlyph);
        Assert.Equal("Scheduled tasks", indicator.ToolTip);
    }

    [Fact]
    public async Task TogglePause_PersistsAndShowsPauseIcon()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var hostGuid = Guid.NewGuid();
        await AddHostProfileAsync(dataAccessLayer, hostGuid);
        var hostId = new EntityId(hostGuid);
        var service = CreateService(dataAccessLayer);

        using var indicator = new ScheduledToolsPauseIndicatorViewModel(service, hostId);

        var glyphChanges = 0;
        indicator.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ScheduledToolsPauseIndicatorViewModel.ButtonGlyph))
            {
                glyphChanges++;
            }
        };

        await indicator.TogglePauseAsync();

        Assert.True(indicator.IsPaused);
        Assert.Equal("⏸", indicator.ButtonGlyph);
        Assert.Equal("Scheduled tasks (paused)", indicator.ToolTip);
        Assert.True(glyphChanges >= 1);

        await indicator.TogglePauseAsync();

        Assert.False(indicator.IsPaused);
        Assert.Equal("⏱", indicator.ButtonGlyph);
    }

    [Fact]
    public async Task ExternalPauseStateChange_UpdatesGlyph()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var hostGuid = Guid.NewGuid();
        await AddHostProfileAsync(dataAccessLayer, hostGuid);
        var hostId = new EntityId(hostGuid);
        var service = CreateService(dataAccessLayer);

        using var indicator = new ScheduledToolsPauseIndicatorViewModel(service, hostId);

        // A pause performed through the service (e.g. from the scheduled tasks tab) updates the button.
        await service.SetPausedAsync(hostId, paused: true);

        Assert.True(indicator.IsPaused);
        Assert.Equal("⏸", indicator.ButtonGlyph);
    }

    [Fact]
    public async Task Dispose_StopsObservingPauseState()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var hostGuid = Guid.NewGuid();
        await AddHostProfileAsync(dataAccessLayer, hostGuid);
        var hostId = new EntityId(hostGuid);
        var service = CreateService(dataAccessLayer);

        var indicator = new ScheduledToolsPauseIndicatorViewModel(service, hostId);
        indicator.Dispose();

        var glyphChanged = false;
        indicator.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ScheduledToolsPauseIndicatorViewModel.ButtonGlyph))
            {
                glyphChanged = true;
            }
        };

        await service.SetPausedAsync(hostId, paused: true);

        Assert.False(glyphChanged);
    }
}

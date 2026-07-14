using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using Phantom.Workspaces.Converters;
using Phantom.Workspaces.ViewModels;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Tests;

public sealed class StatusBadgeCardTests
{
    [PhantomAvaloniaFact]
    public async Task FieldEditorFactory_BuildsStatusBadgeForAnnotatedTaskStatus()
    {
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            TestContext.Current.CancellationToken);
        var entityTypeViewCatalog = await EntityTypeViewCatalog.CreateAsync(broker);
        var factory = new FieldEditorFactory(broker, entityTypeViewCatalog);

        using var document = JsonDocument.Parse(
            """
            {
              "entity-id": "c0d1e2f3-7a8b-4c9d-9e0f-6a7b8c9d0e1f",
              "entity-types": ["entity", "task"],
              "status": "completed"
            }
            """);

        var badges = await factory.BuildStatusBadgesAsync(document.RootElement.Clone());

        var badge = Assert.Single(badges);
        Assert.Equal("completed", badge.StatusValue);
        Assert.Equal("Theme.Status.Good", badge.BrushKey);
        Assert.Equal("status: completed", badge.Tooltip);
    }

    [PhantomAvaloniaFact]
    public async Task FieldEditorFactory_BuildsBadStatusBadgeForBlockedTask()
    {
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            TestContext.Current.CancellationToken);
        var entityTypeViewCatalog = await EntityTypeViewCatalog.CreateAsync(broker);
        var factory = new FieldEditorFactory(broker, entityTypeViewCatalog);

        using var document = JsonDocument.Parse(
            """
            {
              "entity-id": "c0d1e2f3-7a8b-4c9d-9e0f-6a7b8c9d0e1f",
              "entity-types": ["entity", "task"],
              "status": "blocked"
            }
            """);

        var badges = await factory.BuildStatusBadgesAsync(document.RootElement.Clone());

        var badge = Assert.Single(badges);
        Assert.Equal("Theme.Status.Bad", badge.BrushKey);
    }

    [PhantomAvaloniaFact]
    public async Task FieldEditorFactory_ProducesNoStatusBadgeForUnannotatedEntity()
    {
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            TestContext.Current.CancellationToken);
        var entityTypeViewCatalog = await EntityTypeViewCatalog.CreateAsync(broker);
        var factory = new FieldEditorFactory(broker, entityTypeViewCatalog);

        using var document = JsonDocument.Parse(
            """
            {
              "entity-id": "c0d1e2f3-7a8b-4c9d-9e0f-6a7b8c9d0e1f",
              "entity-types": ["entity", "note"],
              "names": [["notes", "no-status"]]
            }
            """);

        var badges = await factory.BuildStatusBadgesAsync(document.RootElement.Clone());

        Assert.Empty(badges);
    }

    [PhantomAvaloniaFact]
    public void StatusBrushKeyConverter_ResolvesKnownKeyToConfiguredBrush()
    {
        var configuredBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
        Application.Current!.Resources["Theme.Status.Good"] = configuredBrush;

        var resolved = StatusBrushKeyConverter.Instance.Convert(
            "Theme.Status.Good",
            typeof(IBrush),
            parameter: null,
            CultureInfo.InvariantCulture);

        Assert.Same(configuredBrush, resolved);
    }

    [PhantomAvaloniaFact]
    public void StatusBrushKeyConverter_UnknownKey_ReturnsTransparent()
    {
        var resolved = StatusBrushKeyConverter.Instance.Convert(
            "Theme.Status.Does.Not.Exist",
            typeof(IBrush),
            parameter: null,
            CultureInfo.InvariantCulture);

        Assert.Equal(Brushes.Transparent, resolved);
    }
}

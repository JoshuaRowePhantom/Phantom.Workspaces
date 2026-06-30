using Avalonia.Headless.XUnit;
using Phantom.Workspaces.Services.Updates;

namespace Phantom.Workspaces.Tests.Updates;

public sealed class TrayIconImageFactoryTests
{
    [AvaloniaFact]
    public void Create_WithoutUpdate_ReturnsIcon()
    {
        var icon = TrayIconImageFactory.Create(updateAvailable: false);
        Assert.NotNull(icon);
    }

    [AvaloniaFact]
    public void Create_WithUpdate_ReturnsIcon()
    {
        var icon = TrayIconImageFactory.Create(updateAvailable: true);
        Assert.NotNull(icon);
    }

    [AvaloniaFact]
    public void Render_WithoutUpdate_ReturnsReadableStreamAtPositionZero()
    {
        using var stream = TrayIconImageFactory.Render(updateAvailable: false);
        Assert.NotNull(stream);
        Assert.True(stream.CanRead);
        Assert.Equal(0, stream.Position);
    }

    [AvaloniaFact]
    public void Render_WithUpdate_ReturnsReadableStreamAtPositionZero()
    {
        using var stream = TrayIconImageFactory.Render(updateAvailable: true);
        Assert.NotNull(stream);
        Assert.True(stream.CanRead);
        Assert.Equal(0, stream.Position);
    }

    [AvaloniaFact]
    public void Render_UpdateAvailable_DifferentBytesFromNoUpdate()
    {
        using var withoutUpdate = TrayIconImageFactory.Render(updateAvailable: false);
        using var withUpdate = TrayIconImageFactory.Render(updateAvailable: true);
        Assert.NotEqual(withoutUpdate.ToArray(), withUpdate.ToArray());
    }
}

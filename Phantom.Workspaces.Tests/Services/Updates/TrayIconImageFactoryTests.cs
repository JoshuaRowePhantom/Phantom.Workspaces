using Avalonia.Headless.XUnit;
using Phantom.Workspaces.Services.Updates;

using Phantom.Workspaces.Testing.Gui;

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

    [AvaloniaFact]
    public void RenderPixelBuffer_WithoutUpdate_TopLeftPixelIsTransparent()
    {
        var pixels = TrayIconImageFactory.RenderPixelBuffer(updateAvailable: false);
        // BGRA8888 premultiplied: alpha is at byte index 3 of the first pixel
        Assert.Equal(0, pixels[3]);
    }

    [AvaloniaFact]
    public void ComputedEmojiFontSize_ScaledToFillIcon()
    {
        // The auto-scaled font size must be larger than the original hardcoded 18,
        // demonstrating the glyph is scaled to fill the 32×32 icon bounds.
        Assert.True(TrayIconImageFactory.ComputedEmojiFontSize > 18,
            $"Expected auto-scaled font size > 18 to fill icon, got {TrayIconImageFactory.ComputedEmojiFontSize:F1}");
    }

    [AvaloniaFact]
    public void RenderPixelBuffer_BrainGlyphOccupiesLargeFractionOfIcon()
    {
        var pixels = TrayIconImageFactory.RenderPixelBuffer(updateAvailable: false);
        int nonTransparent = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] > 0)
                nonTransparent++;
        }
        // At least 10% of the 32×32 icon area must be non-transparent.
        // The auto-scaled glyph must visibly occupy a large fraction of the icon.
        int minimumCoverage = 32 * 32 / 10;
        Assert.True(nonTransparent >= minimumCoverage,
            $"Expected >= {minimumCoverage} non-transparent pixels but found {nonTransparent}");
    }
}

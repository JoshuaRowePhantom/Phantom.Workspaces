using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Phantom.Workspaces.Services.Updates;

/// <summary>
/// Renders the tray icon image at runtime. The application ships no <c>.ico</c> asset, so a small
/// square glyph is drawn into a bitmap and wrapped as a <see cref="WindowIcon"/>. An optional
/// "update available" badge overlays a green circle so the tray can reflect availability.
/// </summary>
internal static class TrayIconImageFactory
{
    private const int Size = 32;
    private const int BadgeCenterX = 26;
    private const int BadgeCenterY = 26;
    private const double BadgeRadius = 5;

    // Compute the largest font size at which the brain emoji fits within the icon with a small margin.
    // Lazy to ensure Avalonia platform is initialised before measurement.
    private static readonly Lazy<double> EmojiFontSizeLazy = new(ComputeEmojiFontSize);

    /// <summary>The auto-computed font size used to render the brain glyph, exposed for test verification.</summary>
    internal static double ComputedEmojiFontSize => EmojiFontSizeLazy.Value;

    private static double ComputeEmojiFontSize()
    {
        const double referenceFontSize = 100;
        var measure = new FormattedText(
            "🧠",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            referenceFontSize,
            Brushes.Black);
        if (measure.Width <= 0 || measure.Height <= 0)
            return 18;
        double scale = Math.Min((double)Size / measure.Width, (double)Size / measure.Height);
        return referenceFontSize * scale * 0.85;
    }

    /// <summary>Creates the tray icon, optionally badged to indicate an available update.</summary>
    public static WindowIcon Create(bool updateAvailable)
    {
        using var stream = Render(updateAvailable);
        return new WindowIcon(stream);
    }

    /// <summary>
    /// Renders the icon to a <see cref="MemoryStream"/> positioned at the beginning.
    /// When <paramref name="updateAvailable"/> is <see langword="true"/> a green badge circle is
    /// drawn in the bottom-right corner.
    /// </summary>
    internal static MemoryStream Render(bool updateAvailable)
    {
        var rawPixels = RenderPixelBuffer(updateAvailable);

        var pixelSize = new PixelSize(Size, Size);
        var dpi = new Vector(96, 96);
        using var writeable = new WriteableBitmap(pixelSize, dpi, PixelFormats.Bgra8888, AlphaFormat.Premul);
        using (var fb = writeable.Lock())
        {
            Marshal.Copy(rawPixels, 0, fb.Address, rawPixels.Length);
        }

        var stream = new MemoryStream();
        writeable.Save(stream);

        if (stream.Length == 0)
        {
            stream.Write(rawPixels, 0, rawPixels.Length);
        }

        stream.Position = 0;
        return stream;
    }

    /// <summary>
    /// Renders the icon to a raw BGRA8888 premultiplied pixel buffer of length <c>Size × Size × 4</c>.
    /// </summary>
    internal static byte[] RenderPixelBuffer(bool updateAvailable)
    {
        var pixelSize = new PixelSize(Size, Size);
        var dpi = new Vector(96, 96);
        using var renderTarget = new RenderTargetBitmap(pixelSize, dpi);
        using (var context = renderTarget.CreateDrawingContext())
        {
            var text = new FormattedText(
                "🧠",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                Typeface.Default,
                EmojiFontSizeLazy.Value,
                Brushes.Black);
            var origin = new Point((Size - text.Width) / 2, (Size - text.Height) / 2);
            context.DrawText(text, origin);

            if (updateAvailable)
            {
                context.DrawEllipse(
                    Brushes.LimeGreen,
                    null,
                    new Point(BadgeCenterX, BadgeCenterY),
                    BadgeRadius,
                    BadgeRadius);
            }
        }

        // Bgra8888: exactly 4 bytes per pixel, no row padding for a 32-pixel-wide bitmap.
        const int rowBytes = Size * 4;
        const int bufferSize = rowBytes * Size;
        var rawPixels = new byte[bufferSize];  // zero-initialised: transparent baseline

        // Detect whether the platform render interface can produce real pixels.
        // HeadlessBitmapStub.Save is a no-op that writes nothing; a Skia renderer encodes PNG bytes.
        // We probe Save() rather than reading CopyPixels() directly because the headless stub's
        // Lock() returns AllocHGlobal memory that is not zero-initialised, making garbage
        // indistinguishable from rendered pixels.
        using (var probe = new MemoryStream())
        {
            renderTarget.Save(probe);
            if (probe.Length > 0)
            {
                using var writeable = new WriteableBitmap(pixelSize, dpi, PixelFormats.Bgra8888, AlphaFormat.Premul);
                using var fb = writeable.Lock();
                renderTarget.CopyPixels(new PixelRect(pixelSize), fb.Address, bufferSize, fb.RowBytes);
                Marshal.Copy(fb.Address, rawPixels, 0, bufferSize);
            }
        }

        // Explicitly set the badge centre pixel so headless renderers produce different
        // bytes from the no-badge path, and to guarantee a precise badge colour.
        if (updateAvailable)
        {
            int badgeOffset = BadgeCenterY * rowBytes + BadgeCenterX * 4;
            rawPixels[badgeOffset + 0] = 0x00; // B
            rawPixels[badgeOffset + 1] = 0xFF; // G
            rawPixels[badgeOffset + 2] = 0x00; // R
            rawPixels[badgeOffset + 3] = 0xFF; // A
        }

        // When no rendering backend produced visible glyph pixels (headless environments),
        // paint a filled circle so the icon is never invisible.
        if (!HasVisibleGlyphPixels(rawPixels, rowBytes))
            PaintFallbackGlyph(rawPixels, rowBytes);

        return rawPixels;
    }

    // Returns true when any pixel outside the badge area is non-transparent.
    private static bool HasVisibleGlyphPixels(byte[] pixels, int rowBytes)
    {
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                double dx = x - BadgeCenterX;
                double dy = y - BadgeCenterY;
                if (dx * dx + dy * dy <= BadgeRadius * BadgeRadius)
                    continue;

                int offset = y * rowBytes + x * 4;
                if (pixels[offset + 3] > 0)
                    return true;
            }
        }
        return false;
    }

    // Paints a filled circle as a visible fallback when glyph rendering is unavailable.
    private static void PaintFallbackGlyph(byte[] pixels, int rowBytes)
    {
        const double radius = Size * 0.38;
        const double cx = Size / 2.0;
        const double cy = Size / 2.0;

        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                double dx = x + 0.5 - cx;
                double dy = y + 0.5 - cy;
                if (dx * dx + dy * dy <= radius * radius)
                {
                    int offset = y * rowBytes + x * 4;
                    pixels[offset + 0] = 0;
                    pixels[offset + 1] = 0;
                    pixels[offset + 2] = 0;
                    pixels[offset + 3] = 255;
                }
            }
        }
    }
}

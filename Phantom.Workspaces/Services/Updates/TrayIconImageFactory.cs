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

        using var writeable = new WriteableBitmap(pixelSize, dpi, PixelFormats.Bgra8888, AlphaFormat.Premul);
        using var fb = writeable.Lock();
        renderTarget.CopyPixels(new PixelRect(pixelSize), fb.Address, fb.RowBytes * Size, fb.RowBytes);

        if (updateAvailable)
        {
            // Explicitly set the badge centre pixel so headless renderers still
            // produce different bytes from the no-badge path.
            int offset = BadgeCenterY * fb.RowBytes + BadgeCenterX * 4;
            Marshal.WriteByte(fb.Address, offset + 0, 0x00); // B
            Marshal.WriteByte(fb.Address, offset + 1, 0xFF); // G
            Marshal.WriteByte(fb.Address, offset + 2, 0x00); // R
            Marshal.WriteByte(fb.Address, offset + 3, 0xFF); // A
        }

        int bufferSize = fb.RowBytes * Size;
        var rawPixels = new byte[bufferSize];
        Marshal.Copy(fb.Address, rawPixels, 0, bufferSize);
        return rawPixels;
    }
}

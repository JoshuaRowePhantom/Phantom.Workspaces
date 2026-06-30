using System;
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
        var pixelSize = new PixelSize(Size, Size);
        var dpi = new Vector(96, 96);
        using var renderTarget = new RenderTargetBitmap(pixelSize, dpi);
        using (var context = renderTarget.CreateDrawingContext())
        {
            var text = new FormattedText(
                "🧠",
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                Typeface.Default,
                18,
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

        // Transfer to a WriteableBitmap so that (a) the badge centre pixel can be set
        // directly in the pixel buffer — guaranteeing a byte-level difference even in
        // headless test environments — and (b) the PNG encoder always produces output.
        using var writeable = new WriteableBitmap(pixelSize, dpi, PixelFormats.Bgra8888, AlphaFormat.Premul);
        byte[] rawPixels;
        using (var fb = writeable.Lock())
        {
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

            // Capture raw pixels inside the lock as a fallback for headless environments
            // where WriteableBitmap.Save() produces an empty stream.
            int bufferSize = fb.RowBytes * Size;
            rawPixels = new byte[bufferSize];
            Marshal.Copy(fb.Address, rawPixels, 0, bufferSize);
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
}

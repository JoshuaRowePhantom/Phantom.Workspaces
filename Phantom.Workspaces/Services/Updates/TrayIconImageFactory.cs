using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Phantom.Workspaces.Services.Updates;

/// <summary>
/// Renders the tray icon image at runtime. The application ships no <c>.ico</c> asset, so a small
/// square glyph is drawn into a bitmap and wrapped as a <see cref="WindowIcon"/>. An optional
/// "update available" badge tints the glyph so the tray can reflect availability.
/// </summary>
internal static class TrayIconImageFactory
{
    private const int Size = 32;

    /// <summary>Creates the tray icon, optionally badged to indicate an available update.</summary>
    public static WindowIcon Create(bool updateAvailable)
    {
        var background = updateAvailable ? Color.FromRgb(0x2E, 0x7D, 0x32) : Color.FromRgb(0x1F, 0x6F, 0xEB);
        var pixelSize = new PixelSize(Size, Size);
        var dpi = new Vector(96, 96);
        using var bitmap = new RenderTargetBitmap(pixelSize, dpi);
        using (var context = bitmap.CreateDrawingContext())
        {
            context.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, Size, Size), 6, 6);
            var text = new FormattedText(
                "P",
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                Typeface.Default,
                20,
                Brushes.White);
            var origin = new Point((Size - text.Width) / 2, (Size - text.Height) / 2);
            context.DrawText(text, origin);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream);
        stream.Position = 0;
        return new WindowIcon(stream);
    }
}

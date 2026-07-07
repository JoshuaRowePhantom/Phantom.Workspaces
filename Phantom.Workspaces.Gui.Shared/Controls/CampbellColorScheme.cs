using Avalonia.Media;

namespace Phantom.Workspaces.Gui.Shared.Controls;

/// <summary>
/// The 18 canonical colors from Windows Terminal's Campbell color scheme (16 ANSI + Background + Foreground).
/// </summary>
internal static class CampbellColorScheme
{
    // Background / Foreground
    public static readonly Color Background = Color.FromRgb(0x0C, 0x0C, 0x0C);
    public static readonly Color Foreground = Color.FromRgb(0xCC, 0xCC, 0xCC);

    // Normal (dim) ANSI 0–7
    public static readonly Color Black   = Color.FromRgb(0x0C, 0x0C, 0x0C);
    public static readonly Color Red     = Color.FromRgb(0xC5, 0x0F, 0x1F);
    public static readonly Color Green   = Color.FromRgb(0x13, 0xA1, 0x0E);
    public static readonly Color Yellow  = Color.FromRgb(0xC1, 0x9C, 0x00);
    public static readonly Color Blue    = Color.FromRgb(0x00, 0x37, 0xDA);
    public static readonly Color Magenta = Color.FromRgb(0x88, 0x17, 0x98);
    public static readonly Color Cyan    = Color.FromRgb(0x3A, 0x96, 0xDD);
    public static readonly Color White   = Color.FromRgb(0xCC, 0xCC, 0xCC);

    // Bright ANSI 0–7
    public static readonly Color BrightBlack   = Color.FromRgb(0x76, 0x76, 0x76);
    public static readonly Color BrightRed     = Color.FromRgb(0xE7, 0x48, 0x56);
    public static readonly Color BrightGreen   = Color.FromRgb(0x16, 0xC6, 0x0C);
    public static readonly Color BrightYellow  = Color.FromRgb(0xF9, 0xF1, 0xA5);
    public static readonly Color BrightBlue    = Color.FromRgb(0x3B, 0x78, 0xFF);
    public static readonly Color BrightMagenta = Color.FromRgb(0xB4, 0x00, 0x9E);
    public static readonly Color BrightCyan    = Color.FromRgb(0x61, 0xD6, 0xD6);
    public static readonly Color BrightWhite   = Color.FromRgb(0xF2, 0xF2, 0xF2);

    // Indexed lookup helpers — indexed 0–7 matching ETerminalColor
    public static readonly Color[] Dim    = [Black, Red, Green, Yellow, Blue, Magenta, Cyan, White];
    public static readonly Color[] Bright = [BrightBlack, BrightRed, BrightGreen, BrightYellow,
                                              BrightBlue, BrightMagenta, BrightCyan, BrightWhite];
}

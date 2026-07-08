using System;

namespace Phantom.Workspaces.Gui.Shared.Encoding;

public static class VtMouseEncoder
{
    public static string Encode(
        int button,
        MouseEventType eventType,
        MouseModifiers modifiers,
        int col,
        int row,
        VtMouseMode mode)
    {
        int cb = CalculateButtonCode(button, eventType, modifiers);

        return mode switch
        {
            VtMouseMode.X10 or VtMouseMode.ButtonTracking or VtMouseMode.AllMotion
                => EncodeLegacy(cb, col, row),
            VtMouseMode.Sgr
                => EncodeSgr(cb, col, row, eventType),
            VtMouseMode.Urxvt
                => EncodeUrxvt(cb, col, row),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };
    }

    private static int CalculateButtonCode(int button, MouseEventType eventType, MouseModifiers modifiers)
    {
        int cb = eventType == MouseEventType.Motion ? 32 : button;

        if ((modifiers & MouseModifiers.Shift) != 0)
            cb += 4;
        if ((modifiers & MouseModifiers.Alt) != 0)
            cb += 8;
        if ((modifiers & MouseModifiers.Ctrl) != 0)
            cb += 16;

        return cb;
    }

    private static string EncodeLegacy(int cb, int col, int row)
    {
        int cx = Math.Min(col + 32, 255);
        int cy = Math.Min(row + 32, 255);
        
        return $"\x1b[M{(char)(cb + 32)}{(char)cx}{(char)cy}";
    }

    private static string EncodeSgr(int cb, int col, int row, MouseEventType eventType)
    {
        char terminator = eventType == MouseEventType.Release ? 'm' : 'M';
        return $"\x1b[<{cb};{col};{row}{terminator}";
    }

    private static string EncodeUrxvt(int cb, int col, int row)
    {
        return $"\x1b[{cb};{col};{row}M";
    }
}

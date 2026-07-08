using System;
using Phantom.Workspaces.Gui.Shared.Encoding;

namespace Phantom.Workspaces.Gui.Shared.Models;

public sealed class TerminalMouseModeState
{
    public VtMouseTrackingMode TrackingMode { get; private set; }
    public bool SgrEncoding { get; private set; }
    public bool UrxvtEncoding { get; private set; }

    public VtMouseMode? EffectiveMode
    {
        get
        {
            if (TrackingMode == VtMouseTrackingMode.None)
                return null;

            if (SgrEncoding)
                return VtMouseMode.Sgr;

            if (UrxvtEncoding)
                return VtMouseMode.Urxvt;

            return TrackingMode switch
            {
                VtMouseTrackingMode.X10 => VtMouseMode.X10,
                VtMouseTrackingMode.Button => VtMouseMode.ButtonTracking,
                VtMouseTrackingMode.AllMotion => VtMouseMode.AllMotion,
                _ => null
            };
        }
    }

    public void Apply(ReadOnlySpan<byte> chunk)
    {
        for (int i = 0; i < chunk.Length - 4; i++)
        {
            // Look for CSI sequence: ESC [ ? <number> h/l
            if (chunk[i] != 0x1b || i + 1 >= chunk.Length || chunk[i + 1] != '[')
                continue;

            i += 2;
            if (i >= chunk.Length || chunk[i] != '?')
                continue;

            i++;
            
            // Parse the decimal number
            int number = 0;
            int digitStart = i;
            while (i < chunk.Length && chunk[i] >= '0' && chunk[i] <= '9')
            {
                number = number * 10 + (chunk[i] - '0');
                i++;
            }

            if (i == digitStart || i >= chunk.Length)
                continue;

            byte terminator = chunk[i];
            if (terminator != 'h' && terminator != 'l')
                continue;

            bool enable = terminator == 'h';

            // Apply the mode change
            switch (number)
            {
                case 1000:
                    TrackingMode = enable ? VtMouseTrackingMode.X10 : VtMouseTrackingMode.None;
                    break;
                case 1002:
                    TrackingMode = enable ? VtMouseTrackingMode.Button : VtMouseTrackingMode.None;
                    break;
                case 1003:
                    TrackingMode = enable ? VtMouseTrackingMode.AllMotion : VtMouseTrackingMode.None;
                    break;
                case 1006:
                    SgrEncoding = enable;
                    break;
                case 1015:
                    UrxvtEncoding = enable;
                    break;
            }
        }
    }

    public void Reset()
    {
        TrackingMode = VtMouseTrackingMode.None;
        SgrEncoding = false;
        UrxvtEncoding = false;
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Avalonia.Media;

namespace Phantom.Workspaces.Gui.Shared.Controls;

/// <summary>
/// Pre-processes VT byte streams to handle sequences that VtNetCore doesn't support.
/// Detects CSI and OSC sequences, handles them, and strips them from the byte stream
/// before passing to VtNetCore.
/// </summary>
internal sealed class TerminalSequenceInterceptor
{
    private readonly TerminalControl _control;
    private readonly Stream _responseStream;

    public TerminalSequenceInterceptor(TerminalControl control, Stream responseStream)
    {
        _control = control;
        _responseStream = responseStream;
    }

    /// <summary>
    /// Processes a chunk of bytes, handling supported sequences and returning
    /// the filtered chunk to pass to VtNetCore.
    /// </summary>
    public byte[] Process(byte[] chunk)
    {
        var output = new List<byte>();
        int i = 0;

        while (i < chunk.Length)
        {
            // Check for ESC
            if (chunk[i] == 0x1b && i + 1 < chunk.Length)
            {
                // Check for CSI (ESC [)
                if (chunk[i + 1] == '[')
                {
                    var (handled, consumed) = TryHandleCsi(chunk, i);
                    if (handled)
                    {
                        i += consumed;
                        continue;
                    }
                }
                // Check for OSC (ESC ])
                else if (chunk[i + 1] == ']')
                {
                    var (handled, consumed) = TryHandleOsc(chunk, i);
                    if (handled)
                    {
                        i += consumed;
                        continue;
                    }
                }
            }

            // Not handled - pass through
            output.Add(chunk[i]);
            i++;
        }

        return output.ToArray();
    }

    private (bool handled, int consumed) TryHandleCsi(byte[] chunk, int start)
    {
        // Parse CSI sequence: ESC [ <params> <intermediate> <final>
        int i = start + 2; // Skip ESC [
        var paramBuilder = new StringBuilder();

        // Check for private marker (?, >, =, <)
        char? privateMarker = null;
        if (i < chunk.Length && (chunk[i] == '?' || chunk[i] == '>' || chunk[i] == '=' || chunk[i] == '<'))
        {
            privateMarker = (char)chunk[i];
            i++;
        }

        // Collect parameter bytes (digits, semicolons)
        while (i < chunk.Length && ((chunk[i] >= '0' && chunk[i] <= '9') || chunk[i] == ';'))
        {
            paramBuilder.Append((char)chunk[i]);
            i++;
        }

        // Check for intermediate byte (space)
        bool hasSpace = false;
        if (i < chunk.Length && chunk[i] == ' ')
        {
            hasSpace = true;
            i++;
        }

        // Final byte
        if (i >= chunk.Length)
            return (false, 0);

        char final = (char)chunk[i];
        string paramStr = paramBuilder.ToString();

        // Handle specific sequences
        bool handled = HandleCsiSequence(privateMarker, paramStr, hasSpace, final);
        if (handled)
            return (true, i - start + 1);

        return (false, 0);
    }

    private bool HandleCsiSequence(char? privateMarker, string paramStr, bool hasSpace, char final)
    {
        // Kitty keyboard protocol
        if (privateMarker == '?' && final == 'u')
        {
            // Query: CSI ? u → CSI ? 0 u
            WriteResponse("\x1b[?0u");
            return true;
        }

        if (privateMarker == '>' && final == 'u')
        {
            // Push keyboard flags: CSI > <n> u → silent acknowledgment
            return true;
        }

        if (privateMarker == '<' && final == 'u')
        {
            // Pop keyboard flags: CSI < u → silent acknowledgment
            return true;
        }

        if (privateMarker == '=' && final == 'u')
        {
            // Set keyboard flags: CSI = <flags> ; <mode> u → no-op
            return true;
        }

        // Key modifier option
        if (privateMarker == '>' && final == 'm')
        {
            // Set key modifier option: CSI > <n> m → no-op
            return true;
        }

        if (privateMarker == '?' && final == 'm')
        {
            // Query key modifier option: CSI ? <n> m → CSI > <n> ; 0 m
            WriteResponse($"\x1b[>{paramStr};0m");
            return true;
        }

        // Device attributes
        if (privateMarker == null && final == 'c')
        {
            // Primary device attributes: CSI c
            WriteResponse("\x1b[?64;1;2;6;22c");
            return true;
        }

        if (privateMarker == '>' && final == 'c')
        {
            // Secondary device attributes: CSI > c
            WriteResponse("\x1b[>0;0;0c");
            return true;
        }

        // Device status report
        if (privateMarker == null && final == 'n')
        {
            if (paramStr == "5")
            {
                // Device status: CSI 5 n → CSI 0 n
                WriteResponse("\x1b[0n");
                return true;
            }
            else if (paramStr == "6")
            {
                // Cursor position: CSI 6 n → CSI <row> ; <col> R
                var (row, col) = GetCursorPosition();
                WriteResponse($"\x1b[{row};{col}R");
                return true;
            }
        }

        // DECSCUSR cursor shape
        if (hasSpace && final == 'q')
        {
            if (int.TryParse(paramStr, out int shape) || string.IsNullOrEmpty(paramStr))
            {
                _control.SetCursorShape(string.IsNullOrEmpty(paramStr) ? 0 : shape);
                return true;
            }
        }

        // Synchronized output
        if (privateMarker == '?' && (paramStr == "2026" || paramStr.StartsWith("2026;")) && (final == 'h' || final == 'l'))
        {
            if (final == 'h')
                _control.IncrementSynchronizedOutput();
            else
                _control.DecrementSynchronizedOutput();
            return true;
        }

        return false;
    }

    private (bool handled, int consumed) TryHandleOsc(byte[] chunk, int start)
    {
        // Parse OSC sequence: ESC ] <params> ST
        // ST can be ESC \ or BEL (0x07)
        int i = start + 2; // Skip ESC ]
        var paramBuilder = new StringBuilder();

        // Find terminator
        while (i < chunk.Length)
        {
            if (chunk[i] == 0x07) // BEL
            {
                i++;
                break;
            }
            else if (chunk[i] == 0x1b && i + 1 < chunk.Length && chunk[i + 1] == '\\') // ESC \
            {
                i += 2;
                break;
            }
            else
            {
                paramBuilder.Append((char)chunk[i]);
                i++;
            }
        }

        string oscParams = paramBuilder.ToString();
        bool handled = HandleOscSequence(oscParams);
        if (handled)
            return (true, i - start);

        return (false, 0);
    }

    private bool HandleOscSequence(string oscParams)
    {
        var parts = oscParams.Split(new[] { ';' }, 2);
        if (parts.Length == 0)
            return false;

        if (!int.TryParse(parts[0], out int oscCode))
            return false;

        string value = parts.Length > 1 ? parts[1] : "";

        // Palette color set/reset
        if (oscCode == 4)
        {
            // OSC 4 ; <index> ; <color>
            var colorParts = value.Split(';');
            if (colorParts.Length >= 2 && int.TryParse(colorParts[0], out int index))
            {
                var color = ParseColor(colorParts[1]);
                if (color.HasValue)
                {
                    _control.SetPaletteColor(index, color.Value);
                    return true;
                }
            }
        }
        else if (oscCode == 104)
        {
            // OSC 104 ; <index> - reset palette color
            var colorParts = value.Split(';');
            if (colorParts.Length >= 1 && int.TryParse(colorParts[0], out int index))
            {
                _control.ResetPaletteColor(index);
                return true;
            }
        }
        // Default foreground
        else if (oscCode == 10)
        {
            var color = ParseColor(value);
            if (color.HasValue)
            {
                _control.SetDefaultFg(color.Value);
                return true;
            }
        }
        else if (oscCode == 110)
        {
            _control.ResetDefaultFg();
            return true;
        }
        // Default background
        else if (oscCode == 11)
        {
            var color = ParseColor(value);
            if (color.HasValue)
            {
                _control.SetDefaultBg(color.Value);
                return true;
            }
        }
        else if (oscCode == 111)
        {
            _control.ResetDefaultBg();
            return true;
        }
        // Cursor color
        else if (oscCode == 12)
        {
            var color = ParseColor(value);
            if (color.HasValue)
            {
                _control.SetCursorColor(color.Value);
                return true;
            }
        }
        else if (oscCode == 112)
        {
            _control.ResetCursorColor();
            return true;
        }
        // Shell integration
        else if (oscCode == 133)
        {
            var markParts = value.Split(';');
            if (markParts.Length > 0)
            {
                string markType = markParts[0];
                int? exitCode = markParts.Length > 1 && int.TryParse(markParts[1], out int ec) ? ec : null;

                string? markerName = markType switch
                {
                    "A" => "PromptStart",
                    "B" => "PromptEnd",
                    "C" => "CommandStart",
                    "D" => "CommandEnd",
                    _ => null,
                };

                if (markerName != null)
                {
                    _control.AddShellMark(markerName, exitCode);
                    return true;
                }
            }
        }
        // Working directory
        else if (oscCode == 7)
        {
            // OSC 7 ; file://<path>
            if (value.StartsWith("file://"))
            {
                _control.SetWorkingDirectory(value.Substring(7));
                return true;
            }
        }
        // Hyperlink
        else if (oscCode == 8)
        {
            // OSC 8 ; <params> ; <uri>
            var linkParts = value.Split(new[] { ';' }, 2);
            string uri = linkParts.Length > 1 ? linkParts[1] : "";
            if (string.IsNullOrEmpty(uri))
            {
                _control.CloseHyperlink();
            }
            else
            {
                _control.OpenHyperlink(uri);
            }
            return true;
        }
        // Window title
        else if (oscCode == 0 || oscCode == 2)
        {
            _control.SetTitle(value);
            return true;
        }

        return false;
    }

    private Color? ParseColor(string colorSpec)
    {
        // Parse rgb:RR/GG/BB format
        if (colorSpec.StartsWith("rgb:", StringComparison.OrdinalIgnoreCase))
        {
            var rgb = colorSpec.Substring(4).Split('/');
            if (rgb.Length == 3 &&
                byte.TryParse(rgb[0], NumberStyles.HexNumber, null, out byte r) &&
                byte.TryParse(rgb[1], NumberStyles.HexNumber, null, out byte g) &&
                byte.TryParse(rgb[2], NumberStyles.HexNumber, null, out byte b))
            {
                return Color.FromRgb(r, g, b);
            }
        }

        return null;
    }

    private (int row, int col) GetCursorPosition()
    {
        if (_control.Vtc != null)
        {
            // VtNetCore uses 0-based indexing, but VT reports use 1-based
            return (_control.Vtc.ViewPort.CursorPosition.Row + 1, _control.Vtc.ViewPort.CursorPosition.Column + 1);
        }
        return (1, 1);
    }

    private void WriteResponse(string response)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(response);
        _ = _responseStream.WriteAsync(bytes).AsTask();
    }
}

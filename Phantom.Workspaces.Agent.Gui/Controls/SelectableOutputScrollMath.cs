using Avalonia.Input;

namespace Phantom.Workspaces.Agent.Gui.Controls;

/// <summary>
/// Computes vertical scroll offsets for the selectable chat output in response to navigation keys
/// (Page Up/Down, Home, End). Extracted as a pure function so the behaviour can be unit tested without
/// a rendered scroll viewer.
/// </summary>
public static class SelectableOutputScrollMath
{
    /// <summary>
    /// Returns the new vertical offset for the given navigation <paramref name="key"/>, or
    /// <see langword="null"/> if the key is not a recognised scroll key. Page Up/Down move by one
    /// viewport (less a small overlap), Home scrolls to the top, and End scrolls to the bottom. The
    /// result is always clamped to the scrollable range.
    /// </summary>
    public static double? ComputeVerticalOffset(
        Key key,
        double currentY,
        double viewportHeight,
        double extentHeight)
    {
        var maxOffset = System.Math.Max(0, extentHeight - viewportHeight);

        // Keep a small overlap between pages so the reader does not lose their place.
        var pageStep = System.Math.Max(0, viewportHeight - PageOverlap);

        double target;
        switch (key)
        {
            case Key.PageUp:
                target = currentY - pageStep;
                break;
            case Key.PageDown:
                target = currentY + pageStep;
                break;
            case Key.Home:
                target = 0;
                break;
            case Key.End:
                target = maxOffset;
                break;
            default:
                return null;
        }

        return System.Math.Clamp(target, 0, maxOffset);
    }

    private const double PageOverlap = 32;
}

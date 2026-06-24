using Avalonia.Input;

namespace Phantom.Workspaces.Agent.Gui.Controls;

/// <summary>
/// Computes vertical scroll offsets for the selectable chat output in response to the Home and End
/// keys, which Avalonia's <c>ScrollViewer</c> does not handle itself (it already handles Page Up/Down).
/// Extracted as a pure function so the behaviour can be unit tested without a rendered scroll viewer.
/// </summary>
public static class SelectableOutputScrollMath
{
    /// <summary>
    /// Returns the new vertical offset for the given navigation <paramref name="key"/>, or
    /// <see langword="null"/> if the key is not Home or End. Home scrolls to the top and End scrolls to
    /// the bottom; the result is clamped to the scrollable range.
    /// </summary>
    public static double? ComputeVerticalOffset(
        Key key,
        double viewportHeight,
        double extentHeight)
    {
        var maxOffset = System.Math.Max(0, extentHeight - viewportHeight);
        return key switch
        {
            Key.Home => 0,
            Key.End => maxOffset,
            _ => null,
        };
    }
}

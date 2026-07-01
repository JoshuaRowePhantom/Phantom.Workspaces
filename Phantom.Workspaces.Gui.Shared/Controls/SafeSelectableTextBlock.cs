using Avalonia;
using Avalonia.Controls;

namespace Phantom.Workspaces.Gui.Shared.Controls;

/// <summary>
/// A <see cref="SelectableTextBlock"/> subclass that short-circuits MeasureOverride when
/// availableSize.Width is zero, preventing TextFormatterImpl.PerformTextWrapping from creating
/// one TextLineImpl per character (6 million lines → 5.8 GB allocation) when TextWrapping=Wrap
/// and the control is first-measured with Size(0,0) by Avalonia's layout manager.
/// </summary>
public class SafeSelectableTextBlock : SelectableTextBlock
{
    protected override Size MeasureOverride(Size availableSize)
    {
        if (availableSize.Width == 0)
            return default;
        return base.MeasureOverride(availableSize);
    }
}

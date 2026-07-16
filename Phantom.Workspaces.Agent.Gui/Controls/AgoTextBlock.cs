using Avalonia;
using Avalonia.Controls;
using Phantom.Workspaces.Agent.Gui.Converters;

namespace Phantom.Workspaces.Agent.Gui.Controls;

/// <summary>
/// A reusable <see cref="TextBlock"/> that displays a <see cref="DateTime"/> as a relative-time
/// ("ago") string produced by <see cref="DateTimeAgoConverter"/>. On mouse-over it shows a tooltip
/// with the absolute timestamp formatted as <c>yyyy-MM-dd HH:mm:ss</c>.
/// </summary>
/// <remarks>
/// Bind <see cref="Value"/> to a <see cref="DateTime"/> source. When <see cref="Value"/> is
/// <see langword="null"/> the control renders no text and no tooltip. The relative text only
/// refreshes when <see cref="Value"/> changes (a periodic refresh is out of scope).
/// </remarks>
public sealed class AgoTextBlock : TextBlock
{
    /// <summary>
    /// The absolute-timestamp tooltip format shared by every "ago" label.
    /// </summary>
    public const string AbsoluteFormat = "yyyy-MM-dd HH:mm:ss";

    public static readonly StyledProperty<DateTime?> ValueProperty =
        AvaloniaProperty.Register<AgoTextBlock, DateTime?>(nameof(Value));

    public DateTime? Value
    {
        get => this.GetValue(ValueProperty);
        set => this.SetValue(ValueProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ValueProperty)
        {
            this.UpdateFromValue();
        }
    }

    private void UpdateFromValue()
    {
        if (this.Value is DateTime dateTime)
        {
            this.Text = DateTimeAgoConverter.ToRelativeString(dateTime);
            ToolTip.SetTip(this, dateTime.ToString(AbsoluteFormat));
        }
        else
        {
            this.Text = string.Empty;
            ToolTip.SetTip(this, null);
        }
    }
}

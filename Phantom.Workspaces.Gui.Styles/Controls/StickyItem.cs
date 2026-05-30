using Avalonia;
using Avalonia.Controls;

namespace Phantom.Workspaces.Gui.Styles.Controls;

public static class StickyItem
{
    private sealed class Owner { }

    public static readonly AttachedProperty<int?> RowProperty =
        AvaloniaProperty.RegisterAttached<Owner, Control, int?>("Row");

    public static readonly AttachedProperty<int?> ColumnProperty =
        AvaloniaProperty.RegisterAttached<Owner, Control, int?>("Column");

    public static readonly AttachedProperty<int> BaseRowProperty =
        AvaloniaProperty.RegisterAttached<Owner, Control, int>("BaseRow", defaultValue: 0);

    public static readonly AttachedProperty<int> BaseColumnProperty =
        AvaloniaProperty.RegisterAttached<Owner, Control, int>("BaseColumn", defaultValue: 0);

    public static int? GetRow(Control element) => element.GetValue(RowProperty);
    public static void SetRow(Control element, int? value) => element.SetValue(RowProperty, value);

    public static int? GetColumn(Control element) => element.GetValue(ColumnProperty);
    public static void SetColumn(Control element, int? value) => element.SetValue(ColumnProperty, value);

    public static int GetBaseRow(Control element) => element.GetValue(BaseRowProperty);
    public static void SetBaseRow(Control element, int value) => element.SetValue(BaseRowProperty, value);

    public static int GetBaseColumn(Control element) => element.GetValue(BaseColumnProperty);
    public static void SetBaseColumn(Control element, int value) => element.SetValue(BaseColumnProperty, value);
}

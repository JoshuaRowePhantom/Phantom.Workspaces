using Avalonia;
using Avalonia.Controls;

namespace Phantom.Workspaces.Controls;

public partial class StatusControl : UserControl
{
    public static readonly StyledProperty<Phantom.Workspaces.ViewModels.IStatusItem?> ItemProperty =
        AvaloniaProperty.Register<StatusControl, Phantom.Workspaces.ViewModels.IStatusItem?>(nameof(Item));

    public Phantom.Workspaces.ViewModels.IStatusItem? Item
    {
        get => GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    public StatusControl() => InitializeComponent();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemProperty)
            DataContext = change.NewValue;
    }
}

using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Phantom.Workspaces;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        this.InitializeComponent();
    }

    private void OnCloseClicked(
        object? sender,
        RoutedEventArgs e)
    {
        this.Close();
    }
}

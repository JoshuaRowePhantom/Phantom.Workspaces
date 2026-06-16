using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Phantom.Workspaces;

public partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();
        this.HelpText.Text = CommandLineOptions.GetHelpText();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs args)
    {
        this.Close();
    }
}

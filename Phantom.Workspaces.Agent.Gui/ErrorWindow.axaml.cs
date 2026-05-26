using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Phantom.Workspaces.Agent.Gui;

public partial class ErrorWindow : Window
{
    public ErrorWindow() : this("Unknown error.") { }

    public ErrorWindow(string errorMessage)
    {
        this.InitializeComponent();
        this.ErrorText.Text = errorMessage;
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => this.Close();
}

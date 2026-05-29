using Avalonia.Controls;
using Phantom.Workspaces.Agent.Gui.ViewModels;

namespace Phantom.Workspaces.Agent.Gui.Controls;

public partial class AgentChatEditorControl : UserControl
{
    public AgentChatEditorControl()
    {
        this.InitializeComponent();
    }

    private async void OnToolToggleClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: AgentChatToolViewModel tool } checkBox)
        {
            return;
        }

        await tool.SetEnabledAsync(checkBox.IsChecked == true);
    }
}

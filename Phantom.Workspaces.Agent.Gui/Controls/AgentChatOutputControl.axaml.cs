using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Phantom.Workspaces.Agent.Gui.ViewModels;

namespace Phantom.Workspaces.Agent.Gui.Controls;

public partial class AgentChatOutputControl : UserControl
{
    public AgentChatOutputControl()
    {
        this.InitializeComponent();
    }

    private async void OnHistoryImageClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not ChatHistoryImageViewModel image || image.Preview is null)
        {
            return;
        }

        await ImagePreviewPresenter.ShowAsync(this, image.Preview, image.Label);
    }
}

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Phantom.Workspaces.Agent.Gui.ViewModels;

namespace Phantom.Workspaces.Agent.Gui.Controls;

public partial class AgentChatOutputControl : UserControl
{
    private bool hasAppliedInitialOutputScroll;

    public AgentChatOutputControl()
    {
        this.InitializeComponent();
        this.Loaded += this.OnLoaded;
    }

    private async void OnHistoryImageClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not ChatHistoryImageViewModel image || image.Preview is null)
        {
            return;
        }

        await ImagePreviewPresenter.ShowAsync(this, image.Preview, image.Label);
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (this.hasAppliedInitialOutputScroll)
        {
            return;
        }

        this.hasAppliedInitialOutputScroll = true;
        Dispatcher.UIThread.Post(this.ScrollHistoryToBottom, DispatcherPriority.Background);
    }

    private void ScrollHistoryToBottom()
    {
        this.HistoryScroll.Offset = new Avalonia.Vector(this.HistoryScroll.Offset.X, double.MaxValue);
    }
}

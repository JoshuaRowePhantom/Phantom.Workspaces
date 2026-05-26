using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;

namespace Phantom.Workspaces.Agent.Gui;

public partial class ImagePreviewWindow : Window
{
    private readonly Bitmap? bitmap;

    public ImagePreviewWindow()
    {
        this.InitializeComponent();
    }

    public ImagePreviewWindow(Bitmap bitmap, string title)
    {
        this.bitmap = bitmap;
        this.InitializeComponent();
        this.Title = title;
        this.PreviewImage.Source = bitmap;
    }

    private async void OnCopyClicked(object? sender, RoutedEventArgs e)
    {
        var clipboard = this.Clipboard;
        if (clipboard is null || this.bitmap is null)
        {
            return;
        }

        await clipboard.SetBitmapAsync(this.bitmap);
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
    {
        this.Close();
    }
}

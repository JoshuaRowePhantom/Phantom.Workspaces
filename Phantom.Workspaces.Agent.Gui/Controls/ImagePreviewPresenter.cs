using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace Phantom.Workspaces.Agent.Gui.Controls;

internal static class ImagePreviewPresenter
{
    public static async Task ShowAsync(Control owner, Bitmap bitmap, string title)
    {
        if (TopLevel.GetTopLevel(owner) is not Window window)
        {
            return;
        }

        var previewWindow = new ImagePreviewWindow(bitmap, title);
        await previewWindow.ShowDialog(window);
    }
}

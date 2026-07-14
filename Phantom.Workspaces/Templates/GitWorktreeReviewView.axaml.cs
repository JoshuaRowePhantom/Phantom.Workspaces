using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Templates;

public partial class GitWorktreeReviewView : UserControl
{
    public GitWorktreeReviewView()
    {
        InitializeComponent();
    }

    private async void OnCopyCommitShaClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control control)
        {
            return;
        }

        if (control.DataContext is not GitCommitModel commit)
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return;
        }

        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText(commit.Oid));
        await clipboard.SetDataAsync(data);
    }
}

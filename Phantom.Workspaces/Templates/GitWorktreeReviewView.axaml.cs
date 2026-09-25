using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Templates;

public partial class GitWorktreeReviewView : UserControl
{
    private GitWorktreeReviewWorkspaceTabViewModel? review;
    private Vector? previousDiffOffset;

    public GitWorktreeReviewView()
    {
        InitializeComponent();
        this.DataContextChanged += (_, _) =>
            this.SetReview(this.IsAttachedToVisualTree() ? this.DataContext as GitWorktreeReviewWorkspaceTabViewModel : null);
        this.AttachedToVisualTree += (_, _) =>
            this.SetReview(this.DataContext as GitWorktreeReviewWorkspaceTabViewModel);
        this.DetachedFromVisualTree += (_, _) => this.SetReview(null);
    }

    private void SetReview(GitWorktreeReviewWorkspaceTabViewModel? replacement)
    {
        if (ReferenceEquals(this.review, replacement))
        {
            return;
        }

        if (this.review is not null)
        {
            this.review.DiffRowsReplacing -= this.OnDiffRowsReplacing;
            this.review.PropertyChanged -= this.OnReviewPropertyChanged;
        }

        this.review = replacement;
        if (this.review is not null)
        {
            this.review.DiffRowsReplacing += this.OnDiffRowsReplacing;
            this.review.PropertyChanged += this.OnReviewPropertyChanged;
        }
    }

    private void OnDiffRowsReplacing(object? sender, EventArgs e)
    {
        var rows = this.FindControl<ListBox>("DiffRowsList");
        this.previousDiffOffset = rows?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()?.Offset;
    }

    private void OnReviewPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(GitWorktreeReviewWorkspaceTabViewModel.DiffRows)
            || this.previousDiffOffset is not { } offset)
        {
            return;
        }

        this.previousDiffOffset = null;
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(sender, this.review))
            {
                return;
            }

            var rows = this.FindControl<ListBox>("DiffRowsList");
            var scroller = rows?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (scroller is not null)
            {
                scroller.Offset = new Vector(
                    Math.Min(offset.X, Math.Max(0, scroller.Extent.Width - scroller.Viewport.Width)),
                    Math.Min(offset.Y, Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height)));
            }
        }, DispatcherPriority.Loaded);
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

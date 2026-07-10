using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Phantom.Workspaces.Agent.Gui.Controls;

public partial class QueueComposerControl : UserControl
{
    private QueueComposerViewModel? subscribedViewModel;

    public QueueComposerControl()
    {
        this.InitializeComponent();
        DragDrop.SetAllowDrop(this.InputBox, true);
        this.InputBox.AddHandler(
            InputElement.KeyDownEvent,
            this.InputBox_KeyDown,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        this.InputBox.AddHandler(
            TextBox.PastingFromClipboardEvent,
            this.InputBox_PastingFromClipboard,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            handledEventsToo: true);
        DragDrop.AddDropHandler(this.InputBox, this.InputBox_Drop);
        this.DataContextChanged += this.OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (this.subscribedViewModel is not null)
        {
            this.subscribedViewModel.FocusPrimaryControlRequested -= this.OnFocusPrimaryControlRequested;
        }

        this.subscribedViewModel = this.DataContext as QueueComposerViewModel;
        if (this.subscribedViewModel is not null)
        {
            this.subscribedViewModel.FocusPrimaryControlRequested += this.OnFocusPrimaryControlRequested;
        }
    }

    private void OnFocusPrimaryControlRequested(object? sender, EventArgs e)
    {
        this.InputBox.Focus();
    }

    private void InputBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (this.DataContext is not QueueComposerViewModel vm)
        {
            return;
        }

        var caretLine = 0;
        TextBox? textBox = null;
        if (sender is TextBox tb)
        {
            textBox = tb;
            vm.InputText = tb.Text ?? string.Empty;

            if (e.Key == Key.Back
                && vm.TryRemoveImageAttachmentBeforeCaret(
                    tb.Text ?? string.Empty,
                    tb.CaretIndex,
                    out var updatedText,
                    out var updatedCaretIndex))
            {
                tb.Text = updatedText;
                tb.CaretIndex = updatedCaretIndex;
                e.Handled = true;
                return;
            }

            var text = tb.Text ?? string.Empty;
            var clampedCaret = Math.Min(tb.CaretIndex, text.Length);
            foreach (var c in text.AsSpan(0, clampedCaret))
            {
                if (c == '\n')
                {
                    caretLine++;
                }
            }

            // In normal mode there are no '\n' characters, so caretLine above is always 0
            // even when the text box wraps its content across multiple visual lines.
            // Query the TextPresenter's layout to get the actual visual line index so that
            // Up does not hijack history navigation when the caret is not on the first line.
            if (caretLine == 0)
            {
                var presenter = tb.GetVisualDescendants()
                    .OfType<TextPresenter>()
                    .FirstOrDefault();
                if (presenter is not null)
                {
                    var textLines = presenter.TextLayout.TextLines;
                    for (var lineIndex = 1; lineIndex < textLines.Count; lineIndex++)
                    {
                        if (clampedCaret > textLines[lineIndex].FirstTextSourceIndex)
                        {
                            caretLine = lineIndex;
                        }
                        else
                        {
                            break;
                        }
                    }
                }
            }
        }

        if (HandleInputKey(vm, e.Key, e.KeyModifiers, caretLine, out var newText, out var newCaretIndex))
        {
            if (newText is not null && textBox is not null)
            {
                textBox.Text = newText;
                textBox.CaretIndex = newCaretIndex;
                vm.InputText = newText;
            }

            e.Handled = true;
        }
    }

    private async void InputBox_PastingFromClipboard(object? sender, RoutedEventArgs e)
    {
        if (this.DataContext is not QueueComposerViewModel vm)
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return;
        }

        var handled = await this.TryAppendImagesAsync(vm, clipboard);
        if (handled)
        {
            e.Handled = true;
            this.SyncTextBoxText(vm);
        }
    }

    private async void InputBox_Drop(object? sender, DragEventArgs e)
    {
        if (this.DataContext is not QueueComposerViewModel vm)
        {
            return;
        }

        if (await this.TryAppendImagesAsync(vm, e.DataTransfer))
        {
            e.Handled = true;
            this.SyncTextBoxText(vm);
        }
    }

    internal static bool HandleInputKey(QueueComposerViewModel vm, Key key, KeyModifiers keyModifiers)
        => HandleInputKey(vm, key, keyModifiers, caretLine: 0, out _, out _);

    internal static bool HandleInputKey(
        QueueComposerViewModel vm,
        Key key,
        KeyModifiers keyModifiers,
        int caretLine,
        out string? newText,
        out int newCaretIndex)
    {
        newText = null;
        newCaretIndex = 0;

        // Completions popup intercepts Tab, Esc, and arrow keys.
        if (vm.Completions.IsVisible)
        {
            if (key == Key.Escape)
            {
                vm.Completions.Dismiss();
                return true;
            }

            if (key == Key.Down)
            {
                vm.Completions.SelectNext();
                return true;
            }

            if (key == Key.Up)
            {
                vm.Completions.SelectPrevious();
                return true;
            }

            if (key == Key.Tab)
            {
                if (vm.Completions.SelectedItem is null)
                {
                    vm.Completions.SelectNext();
                }
                else
                {
                    var accepted = vm.Completions.Accept();
                    if (accepted is not null)
                    {
                        newText = "/" + accepted;
                        newCaretIndex = newText.Length;
                        vm.InputText = newText;
                    }
                }

                return true;
            }

            // Block Enter from submitting while the popup is open.
            if (key == Key.Enter || key == Key.Return)
            {
                return true;
            }
        }

        if (key == Key.Up)
        {
            if (vm.TryNavigateHistoryUp(caretLine, out var histText, out var histCaret))
            {
                newText = histText;
                newCaretIndex = histCaret;
                return true;
            }
        }

        if (key == Key.Down)
        {
            if (vm.TryNavigateHistoryDown(out var histText, out var histCaret))
            {
                newText = histText;
                newCaretIndex = histCaret;
                return true;
            }
        }

        if (key == Key.Enter || key == Key.Return)
        {
            if (vm.IsFormattedMode)
            {
                if (keyModifiers.HasFlag(KeyModifiers.Control))
                {
                    vm.Submit();
                    return true;
                }
            }
            else
            {
                if (keyModifiers.HasFlag(KeyModifiers.Shift))
                {
                    vm.EnterFormattedMode();
                    return true;
                }

                if (keyModifiers.HasFlag(KeyModifiers.Control))
                {
                    vm.Submit();
                    return true;
                }

                vm.Submit();
                return true;
            }
        }
        else if (vm.IsDefaultComposer && key == Key.Q && keyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (keyModifiers.HasFlag(KeyModifiers.Shift))
            {
                return vm.SubmitToNewQueue();
            }
            else
            {
                return vm.SubmitToMostRecentQueue();
            }
        }
        else if (key == Key.Escape && vm.IsFormattedMode)
        {
            vm.ExitFormattedMode();
            return true;
        }

        return false;
    }

    private async Task<bool> TryAppendImagesAsync(QueueComposerViewModel vm, IDataTransfer? dataTransfer)
    {
        if (dataTransfer is null)
        {
            return false;
        }

        var handled = false;

        var bitmap = dataTransfer.TryGetBitmap();
        if (bitmap is not null)
        {
            using (bitmap)
            {
                handled = true;
                vm.AppendImageAttachment(
                    this.BitmapToBytes(bitmap),
                    "image/png",
                    bitmap.PixelSize.Width,
                    bitmap.PixelSize.Height);
            }
        }

        var files = dataTransfer.TryGetFiles();
        if (files is not null)
        {
            foreach (var fileItem in files)
            {
                using (fileItem)
                {
                    if (fileItem is IStorageFile file
                        && await this.TryAppendImageFileAsync(vm, file))
                    {
                        handled = true;
                    }
                }
            }
        }

        return handled;
    }

    private async Task<bool> TryAppendImagesAsync(QueueComposerViewModel vm, IClipboard clipboard)
    {
        var handled = false;

        var bitmap = await clipboard.TryGetBitmapAsync();
        if (bitmap is not null)
        {
            using (bitmap)
            {
                handled = true;
                vm.AppendImageAttachment(
                    this.BitmapToBytes(bitmap),
                    "image/png",
                    bitmap.PixelSize.Width,
                    bitmap.PixelSize.Height);
            }
        }

        var files = await clipboard.TryGetFilesAsync();
        if (files is not null)
        {
            foreach (var fileItem in files)
            {
                using (fileItem)
                {
                    if (fileItem is IStorageFile file
                        && await this.TryAppendImageFileAsync(vm, file))
                    {
                        handled = true;
                    }
                }
            }
        }

        return handled;
    }

    private async Task<bool> TryAppendImageFileAsync(QueueComposerViewModel vm, IStorageFile file)
    {
        using var stream = await file.OpenReadAsync();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);

        var bytes = memory.ToArray();
        try
        {
            using var bitmap = new Bitmap(new MemoryStream(bytes));
            vm.AppendImageAttachment(
                bytes,
                this.GetMediaType(file.Name),
                bitmap.PixelSize.Width,
                bitmap.PixelSize.Height,
                file.Name);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private byte[] BitmapToBytes(Bitmap bitmap)
    {
        using var memory = new MemoryStream();
        bitmap.Save(memory);
        return memory.ToArray();
    }

    private string GetMediaType(string? fileName)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        return extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            ".png" => "image/png",
            _ => "image/png",
        };
    }

    private void SyncTextBoxText(QueueComposerViewModel vm)
    {
        this.InputBox.Text = vm.InputText;
    }
}

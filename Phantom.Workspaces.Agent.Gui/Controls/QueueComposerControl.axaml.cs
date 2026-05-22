using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using System.IO;
using System.Reflection;

namespace Phantom.Workspaces.Agent.Gui.Controls;

public partial class QueueComposerControl : UserControl
{
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
    }

    private void InputBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (this.DataContext is not QueueComposerViewModel vm)
        {
            return;
        }

        if (sender is TextBox textBox)
        {
            vm.InputText = textBox.Text ?? string.Empty;

            if (e.Key == Key.Back
                && vm.TryRemoveImageAttachmentBeforeCaret(
                    textBox.Text ?? string.Empty,
                    textBox.CaretIndex,
                    out var updatedText,
                    out var updatedCaretIndex))
            {
                textBox.Text = updatedText;
                textBox.CaretIndex = updatedCaretIndex;
                e.Handled = true;
                return;
            }
        }

        e.Handled = HandleInputKey(vm, e.Key, e.KeyModifiers);
    }

    private async void InputBox_PastingFromClipboard(object? sender, RoutedEventArgs e)
    {
        if (this.DataContext is not QueueComposerViewModel vm)
        {
            return;
        }

        var handled = await this.TryAppendImagesAsync(vm, this.GetDataTransfer(e));
        if (handled)
        {
            this.TrySetHandled(e);
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
    {
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

                vm.Submit();
                return true;
            }
        }
        else if (vm.IsDefaultComposer && key == Key.Q && keyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (keyModifiers.HasFlag(KeyModifiers.Shift))
            {
                vm.SubmitToNewQueue();
            }
            else
            {
                vm.SubmitToMostRecentQueue();
            }

            return true;
        }
        else if (key == Key.Escape && vm.IsFormattedMode)
        {
            vm.ExitFormattedMode();
            return true;
        }

        return false;
    }

    private IDataTransfer? GetDataTransfer(object eventArgs)
    {
        var eventArgsType = eventArgs.GetType();
        foreach (var propertyName in new[] { "DataObject", "DataTransfer", "Data" })
        {
            var property = eventArgsType.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            if (property?.GetValue(eventArgs) is IDataTransfer dataTransfer)
            {
                return dataTransfer;
            }
        }

        return null;
    }

    private void TrySetHandled(object eventArgs)
    {
        var property = eventArgs.GetType().GetProperty("Handled", BindingFlags.Instance | BindingFlags.Public);
        if (property?.CanWrite == true && property.PropertyType == typeof(bool))
        {
            property.SetValue(eventArgs, true);
        }
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

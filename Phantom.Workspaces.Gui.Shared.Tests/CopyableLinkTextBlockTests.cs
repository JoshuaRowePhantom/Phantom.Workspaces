using System;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Threading;
using Phantom.Workspaces.Gui.Shared.Controls;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Gui.Shared.Tests;

public sealed class CopyableLinkTextBlockTests
{
    [AvaloniaFact]
    public void CopyableLinkTextBlock_NullOrDisabledCommand_DoesNotActivate()
    {
        var command = new RecordingCommand { IsAllowed = false };
        var link = new CopyableLinkTextBlock { Text = "Open link" };
        var window = Show(link);
        try
        {
            Click(window, link);
            link.Focus();
            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            Assert.Null(command.LastParameter);

            link.Command = command;
            Click(window, link);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            Assert.Equal(0, command.Calls);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void CopyableLinkTextBlock_ClickAndKeyboard_ExecuteOnceWithParameter()
    {
        var parameter = new object();
        var command = new RecordingCommand();
        var link = new CopyableLinkTextBlock { Text = "Open link", Command = command, CommandParameter = parameter };
        var window = Show(link);
        try
        {
            Click(window, link);
            Assert.Equal(1, command.Calls);
            Assert.Same(parameter, command.LastParameter);

            link.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            Assert.Equal(3, command.Calls);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void CopyableLinkTextBlock_RightClickAndReleaseOutside_DoNotActivate()
    {
        var command = new RecordingCommand();
        var link = new CopyableLinkTextBlock { Text = "Open link", Command = command };
        var window = Show(link);
        try
        {
            var point = Inside(link, window);
            window.MouseDown(point, MouseButton.Right);
            window.MouseUp(point, MouseButton.Right);
            Assert.Equal(0, command.Calls);

            window.MouseDown(point, MouseButton.Left);
            var outside = new Point(point.X + link.Bounds.Width + 20, point.Y);
            window.MouseMove(outside, RawInputModifiers.LeftMouseButton);
            window.MouseUp(outside, MouseButton.Left);
            Assert.Equal(0, command.Calls);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void CopyableLinkTextBlock_SmallMoveActivates_LargeDragSelectsWithoutActivating()
    {
        var command = new RecordingCommand();
        var link = new CopyableLinkTextBlock { Text = "Selectable reference link", Command = command };
        var window = Show(link);
        try
        {
            var point = Inside(link, window);
            window.MouseDown(point, MouseButton.Left);
            var smallMove = new Point(point.X + 2, point.Y);
            window.MouseMove(smallMove, RawInputModifiers.LeftMouseButton);
            window.MouseUp(smallMove, MouseButton.Left);
            Assert.Equal(1, command.Calls);

            window.MouseDown(point, MouseButton.Left);
            var end = new Point(point.X + link.Bounds.Width - 2, point.Y);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            window.MouseUp(end, MouseButton.Left);
            Assert.NotEmpty(link.SelectedText);
            Assert.Equal(1, command.Calls);
        }
        finally
        {
            window.Close();
        }
    }

    private static Window Show(CopyableLinkTextBlock link)
    {
        link.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        link.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        link.Background = Brushes.Transparent;
        var window = new Window
        {
            Content = new Border { Padding = new Thickness(20), Child = link },
            Width = 380,
            Height = 120,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static Point Inside(CopyableLinkTextBlock link, Window window)
    {
        var origin = link.TranslatePoint(new Point(0, 0), window)!.Value;
        return new Point(origin.X + Math.Min(10, link.Bounds.Width / 2),
            origin.Y + Math.Min(8, link.Bounds.Height / 2));
    }

    private static void Click(Window window, CopyableLinkTextBlock link)
    {
        var point = Inside(link, window);
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
    }

    private sealed class RecordingCommand : ICommand
    {
        public bool IsAllowed { get; set; } = true;
        public int Calls { get; private set; }
        public object? LastParameter { get; private set; }
        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => this.IsAllowed;

        public void Execute(object? parameter)
        {
            this.Calls++;
            this.LastParameter = parameter;
        }
    }
}

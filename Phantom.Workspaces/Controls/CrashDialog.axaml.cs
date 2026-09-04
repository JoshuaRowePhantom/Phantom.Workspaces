using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Phantom.Workspaces.Controls;

public sealed partial class CrashDialog : Window
{
    private readonly Exception? _exception;

    public CrashDialog()
        : this(null, isTerminating: false)
    {
    }

    public CrashDialog(Exception? exception, bool isTerminating)
    {
        _exception = exception;
        InitializeComponent();

        var exceptionText = exception?.ToString() ?? "No exception details available.";
        this.ExceptionTextBox.Text = exceptionText;

        if (isTerminating)
        {
            // When the runtime is already terminating, Ignore is not meaningful.
            this.Title = "⚠ Fatal error — application is terminating";
        }
    }

    internal Task ShowAsync()
    {
        var tcs = new TaskCompletionSource();
        this.Closed += (_, _) => tcs.TrySetResult();
        this.Show();
        return tcs.Task;
    }

    private void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        this.ExceptionTextBox.SelectAll();
        this.ExceptionTextBox.Copy();
        this.ExceptionTextBox.ClearSelection();
    }

    private void OnReportClick(object? sender, RoutedEventArgs e)
    {
        var exText = _exception?.ToString() ?? "Unknown exception";
        var title = Uri.EscapeDataString(
            $"Crash: {_exception?.GetType().Name ?? "Exception"}: {(_exception?.Message ?? "unknown error").Split('\n')[0]}");
        
        // Truncate exception text to ensure URL fits within ShellExecute's ~2048 character limit
        const int MaxBodyChars = 1400;
        string bodyText;
        if (exText.Length > MaxBodyChars)
        {
            bodyText = exText.Substring(0, MaxBodyChars) + "\n\n... (truncated — full stack trace available via Copy button)";
        }
        else
        {
            bodyText = exText;
        }
        
        var body = Uri.EscapeDataString(bodyText);
        var url = $"https://github.com/JoshuaRowePhantom/Phantom.Workspaces/issues/new?title={title}&body={body}";
        _ = TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(new Uri(url));
    }

    private void OnIgnoreClick(object? sender, RoutedEventArgs e)
    {
        this.Close();
    }

    private void OnAbortClick(object? sender, RoutedEventArgs e)
    {
        Environment.FailFast("User-initiated abort after unhandled exception", _exception);
    }
}

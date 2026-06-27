using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace Phantom.Workspaces.Agent.Gui.WebViewTests;

/// <summary>
/// Hosts a real (non-headless) Win32 Avalonia application on a dedicated STA thread so tests can
/// instantiate a native WebView. The Avalonia headless harness cannot host a WebView (it throws
/// <c>RPC_E_CHANGED_MODE</c> on attach), and a native control host additionally requires an
/// application manifest with a supported-OS list (see <c>app.manifest</c>). All UI work runs through
/// <see cref="InvokeAsync"/>; tests synchronize on WebView events (no timing waits).
/// </summary>
public sealed class WebViewAppFixture : IDisposable
{
    private readonly Thread thread;
    private readonly CancellationTokenSource cancellation = new();

    public WebViewAppFixture()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        this.thread = new Thread(() =>
        {
            AppBuilder.Configure<WebViewTestApplication>()
                .UsePlatformDetect()
                .UseSkia()
                .SetupWithoutStarting();
            started.SetResult();
            Dispatcher.UIThread.MainLoop(this.cancellation.Token);
        })
        {
            IsBackground = true,
            Name = "WebViewTestUIThread",
        };
        this.thread.SetApartmentState(ApartmentState.STA);
        this.thread.Start();
        started.Task.Wait(TimeSpan.FromSeconds(60));
    }

    /// <summary>Runs <paramref name="action"/> on the Avalonia UI thread and awaits its completion.</summary>
    public Task InvokeAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await action();
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        return completion.Task;
    }

    public void Dispose()
    {
        this.cancellation.Cancel();
        this.thread.Join(TimeSpan.FromSeconds(5));
        this.cancellation.Dispose();
    }

    private sealed class WebViewTestApplication : Application
    {
        public override void Initialize() => this.Styles.Add(new FluentTheme());
    }
}

[CollectionDefinition(WebViewTestCollection.Name)]
public sealed class WebViewTestCollection : ICollectionFixture<WebViewAppFixture>
{
    public const string Name = "WebView (real Win32) integration";
}

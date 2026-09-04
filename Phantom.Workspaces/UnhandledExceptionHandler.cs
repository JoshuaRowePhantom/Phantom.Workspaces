using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace Phantom.Workspaces;

internal static class UnhandledExceptionHandler
{
    internal static int _dialogActive; // Interlocked flag: 0 = no dialog, 1 = dialog visible

    // Replaceable in tests so the Avalonia dialog is not instantiated during unit test runs.
    internal static Func<Exception?, bool, Task> ShowCrashDialogAsync
        = static (ex, isTerminating) => new Controls.CrashDialog(ex, isTerminating).ShowAsync();

    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

        // #1352: unobserved TaskScheduler exceptions are NOT shown in the crash dialog. They are
        // benign-by-default (the runtime swallows them after finalization) and are already logged +
        // observed by Services.Logging.GlobalExceptionLogging, which subscribes
        // TaskScheduler.UnobservedTaskException during host framework init. Only process-crashing
        // paths (AppDomain / dispatcher) show the crash dialog.
    }

    public static void InstallDispatcherHandler()
    {
        Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;
    }

    internal static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        => ShowOrDiscard(e.ExceptionObject as Exception, isTerminating: e.IsTerminating);

    internal static void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // #1093: record the fault through the centralized helper before showing the crash dialog.
        Services.Logging.GlobalExceptionLogging.OnDispatcherUnhandled(e.Exception);
        e.Handled = true;
        ShowOrDiscard(e.Exception, isTerminating: false);
    }

    internal static void ShowOrDiscard(Exception? ex, bool isTerminating)
    {
        if (Interlocked.CompareExchange(ref _dialogActive, 1, 0) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await ShowCrashDialogAsync(ex, isTerminating);
            }
            finally
            {
                Interlocked.Exchange(ref _dialogActive, 0);
            }
        });
    }
}

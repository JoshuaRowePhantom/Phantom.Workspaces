using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Avalonia.Headless.XUnit;
using Phantom.Workspaces.Controls;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Tests;

public sealed class CrashDialogTests
{
    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void CrashDialog_ShowsExceptionText()
    {
        var exception = new InvalidOperationException("Something went wrong");
        var dialog = new CrashDialog(exception, isTerminating: false);

        Assert.Equal(exception.ToString(), dialog.ExceptionTextBox.Text);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void CrashDialog_NullException_ShowsFallbackText()
    {
        var dialog = new CrashDialog(null, isTerminating: false);

        Assert.Equal("No exception details available.", dialog.ExceptionTextBox.Text);
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void CrashDialog_WhenTerminating_UpdatesTitle()
    {
        var dialog = new CrashDialog(new Exception("fatal"), isTerminating: true);

        Assert.Contains("terminating", dialog.Title, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class UnhandledExceptionHandlerTests
{
    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void OnUnobservedTaskException_SetsObserved()
    {
        UnhandledExceptionHandler._dialogActive = 0;
        UnhandledExceptionHandler.ShowCrashDialogAsync = static (_, _) => Task.CompletedTask;

        var exception = new AggregateException(new Exception("test"));
        var args = new UnobservedTaskExceptionEventArgs(exception);
        Assert.False(args.Observed);

        UnhandledExceptionHandler.OnUnobservedTaskException(null, args);

        Assert.True(args.Observed);

        // Cleanup: reset flag so dispatcher-posted work doesn't bleed into other tests
        UnhandledExceptionHandler._dialogActive = 0;
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void OnDispatcherUnhandledException_SetsHandled()
    {
        UnhandledExceptionHandler._dialogActive = 0;
        UnhandledExceptionHandler.ShowCrashDialogAsync = static (_, _) => Task.CompletedTask;

        // DispatcherUnhandledExceptionEventArgs has an internal constructor (same pattern as
        // TappedEventArgs in EntityCardControlInteractionTests).
        var args = (DispatcherUnhandledExceptionEventArgs)RuntimeHelpers.GetUninitializedObject(
            typeof(DispatcherUnhandledExceptionEventArgs));
        Assert.False(args.Handled);

        UnhandledExceptionHandler.OnDispatcherUnhandledException(null, args);

        Assert.True(args.Handled);

        UnhandledExceptionHandler._dialogActive = 0;
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void ShowOrDiscard_WhenNoDialogActive_SetsDialogActiveFlag()
    {
        UnhandledExceptionHandler._dialogActive = 0;
        UnhandledExceptionHandler.ShowCrashDialogAsync = static (_, _) => Task.CompletedTask;

        UnhandledExceptionHandler.ShowOrDiscard(new Exception("test"), isTerminating: false);

        // The CompareExchange in ShowOrDiscard sets the flag synchronously before posting to the
        // dispatcher, so the flag must be 1 immediately after the call.
        Assert.Equal(1, Volatile.Read(ref UnhandledExceptionHandler._dialogActive));

        UnhandledExceptionHandler._dialogActive = 0;
    }

    [PhantomAvaloniaFact(Timeout = 15_000)]
    public void ShowOrDiscard_WhenDialogAlreadyActive_Discards()
    {
        var factoryCalled = 0;
        UnhandledExceptionHandler._dialogActive = 1;
        UnhandledExceptionHandler.ShowCrashDialogAsync = (_, _) =>
        {
            factoryCalled++;
            return Task.CompletedTask;
        };

        UnhandledExceptionHandler.ShowOrDiscard(new Exception("test"), isTerminating: false);

        // Early-return path: the factory must never be invoked.
        Assert.Equal(0, factoryCalled);
        Assert.Equal(1, Volatile.Read(ref UnhandledExceptionHandler._dialogActive));

        UnhandledExceptionHandler._dialogActive = 0;
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Xunit;
using Xunit.Sdk;
using Xunit.v3;

namespace Phantom.Workspaces.Testing.Gui;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
[XunitTestCaseDiscoverer(typeof(PhantomAvaloniaFactDiscoverer))]
public sealed class PhantomAvaloniaFactAttribute(
    [CallerFilePath] string? sourceFilePath = null,
    [CallerLineNumber] int sourceLineNumber = -1)
    : FactAttribute(sourceFilePath, sourceLineNumber);

public class PhantomAvaloniaFactDiscoverer : AvaloniaFactDiscoverer
{
    protected override IXunitTestCase CreateTestCase(
        ITestFrameworkDiscoveryOptions discoveryOptions,
        IXunitTestMethod testMethod,
        IFactAttribute factAttribute)
    {
        var inner = base.CreateTestCase(discoveryOptions, testMethod, factAttribute);
        return new PhantomAvaloniaTestCase(inner);
    }
}

public sealed class PhantomAvaloniaTestCase : ISelfExecutingXunitTestCase, IXunitSerializable, IAsyncDisposable
{
    // HeadlessUnitTestSession._dispatchTask is the Task.Run backing the queue processor loop.
    // When it exits — whether normally (DisposeAsync) or faulted (unhandled exception, e.g.
    // Dispatcher.ResetForUnitTests() throwing InvalidProgramException) — items already in the
    // queue but not yet processed have their TaskCompletionSource permanently abandoned, hanging
    // every pending PhantomAvaloniaTestCase.Run indefinitely.  We reflect on this private field
    // so that we can register a ContinueWith that fires tcs.TrySetException, converting the
    // hang into a test failure with a diagnostic message.  If the field is renamed in a future
    // Avalonia release the reflect returns null and we fall back to the pre-fix behaviour.
    // See: https://github.com/JoshuaRowePhantom/Phantom.Workspaces/issues/643
    private static readonly FieldInfo? _dispatchTaskField =
        typeof(HeadlessUnitTestSession).GetField(
            "_dispatchTask", BindingFlags.NonPublic | BindingFlags.Instance);

    // HeadlessUnitTestSession._cancellationTokenSource is cancelled by DisposeAsync.  The
    // _dispatchTask safety net above only fires when _dispatchTask actually EXITS.  However,
    // _dispatchTask can be alive-but-stuck (e.g. RunLoop blocked in ExecuteJob on a re-entrant
    // dispatcher call, or PushFrame waiting for frame.Continue=false that never fires).  In
    // those cases _dispatchTask never exits and the ContinueWith never fires.
    // Registering on _cancellationTokenSource.Token gives us a second trigger: when the test
    // framework calls session.DisposeAsync() it cancels this token, which fires our callback
    // synchronously (useSynchronizationContext:false captures no context), unblocking every
    // pending tcs.Task immediately and converting the hang into a test failure.
    // See: https://github.com/JoshuaRowePhantom/Phantom.Workspaces/issues/660
    private static readonly FieldInfo? _cancellationTokenSourceField =
        typeof(HeadlessUnitTestSession).GetField(
            "_cancellationTokenSource", BindingFlags.NonPublic | BindingFlags.Instance);

    // Shared STA thread pool for running tests. With shared-app isolation (no PerTest),
    // all tests in an assembly must run on the same STA thread to avoid cross-thread
    // Dispatcher access errors during HeadlessUnitTestSession.EnsureIsolatedApplication.
    // See: https://github.com/JoshuaRowePhantom/Phantom.Workspaces/issues/815
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Assembly, StaTaskScheduler> _staSchedulers = new();

    private IXunitTestCase _inner;

    [Obsolete("Called by the de-serializer; should only be called by deserializers")]
    public PhantomAvaloniaTestCase() { _inner = null!; }

    public PhantomAvaloniaTestCase(IXunitTestCase inner) { _inner = inner; }

    void IXunitSerializable.Serialize(IXunitSerializationInfo info)
        => info.AddValue("Inner", _inner, _inner.GetType());

    void IXunitSerializable.Deserialize(IXunitSerializationInfo info)
        => _inner = (IXunitTestCase)info.GetValue("Inner")!;

    public async ValueTask<RunSummary> Run(
        ExplicitOption explicitOption,
        IMessageBus messageBus,
        object?[] constructorArguments,
        ExceptionAggregator aggregator,
        CancellationTokenSource cancellationTokenSource)
    {
        // With shared-app isolation (no PerTest), all tests in an assembly must run on the same
        // STA thread to avoid cross-thread Dispatcher access. Get or create the STA scheduler
        // for this assembly.
        var testAssembly = _inner.TestClass?.Class?.Assembly;
        var scheduler = testAssembly != null
            ? _staSchedulers.GetOrAdd(testAssembly, _ => new StaTaskScheduler())
            : null;

        var tcs = new TaskCompletionSource<RunSummary>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (scheduler != null)
        {
            // Run the test on the shared STA thread for this assembly
            _ = Task.Factory.StartNew(
                () =>
                {
                    try
                    {
                        tcs.SetResult(this.RunInnerWithCleanup(
                            explicitOption, messageBus, constructorArguments, aggregator, cancellationTokenSource));
                    }
                    catch (Exception ex)
                    {
                        tcs.SetException(ex);
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.None,
                scheduler);
        }
        else
        {
            // Fallback: no assembly info, use old behavior with a new thread
            var thread = new Thread(() =>
            {
                try
                {
                    tcs.SetResult(this.RunInnerWithCleanup(
                        explicitOption, messageBus, constructorArguments, aggregator, cancellationTokenSource));
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }) { IsBackground = true };
            if (OperatingSystem.IsWindows())
            {
                thread.SetApartmentState(ApartmentState.STA);
            }
            thread.Start();
        }

        // Safety net against HeadlessUnitTestSession queue-processor death (#643).
        // If the session's dispatch loop exits before our background thread's item is processed
        // (e.g. Dispatcher.ResetForUnitTests() throws InvalidProgramException from inside
        // application.Dispose(), killing the loop because only OperationCanceledException is
        // caught), tcs is never resolved and this Run hangs forever.
        // Registering a ContinueWith on the private _dispatchTask converts the hang into a
        // test failure with a diagnostic message.  The TrySetException is a no-op for tests
        // whose tcs was already set by the background thread, so normal runs are unaffected.
        if (_inner.TestClass?.Class?.Assembly is { } assembly)
        {
            var session = HeadlessUnitTestSession.GetOrStartForAssembly(assembly);
            if (_dispatchTaskField?.GetValue(session) is Task dispatchTask)
            {
                _ = dispatchTask.ContinueWith(
                    static (dt, state) =>
                    {
                        var pendingTcs = (TaskCompletionSource<RunSummary>)state!;
                        if (dt.IsFaulted)
                        {
                            pendingTcs.TrySetException(new InvalidOperationException(
                                "HeadlessUnitTestSession queue processor crashed before this test was " +
                                "dispatched. Root cause: application.Dispose() threw a non-cancellation " +
                                "exception (likely Dispatcher.ResetForUnitTests() timed out on pending " +
                                "UIThread jobs). See https://github.com/JoshuaRowePhantom/Phantom.Workspaces/issues/643",
                                dt.Exception!.InnerException ?? dt.Exception));
                        }
                        else
                        {
                            pendingTcs.TrySetException(new InvalidOperationException(
                                "HeadlessUnitTestSession was disposed before this test was dispatched. " +
                                "See https://github.com/JoshuaRowePhantom/Phantom.Workspaces/issues/643"));
                        }
                    },
                    tcs,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            // Second safety net: fire immediately when the session's cancellation token fires
            // (DisposeAsync calls CancelAsync before awaiting _dispatchTask).  This covers the
            // alive-but-stuck case where _dispatchTask never exits — e.g. PushFrame waiting for
            // frame.Continue=false on a dispatcher that silently stopped processing items — so that
            // pending tests fail with a clear message rather than hanging forever.
            if (_cancellationTokenSourceField?.GetValue(session) is CancellationTokenSource sessionCts)
            {
                sessionCts.Token.Register(
                    static state =>
                    {
                        var pendingTcs = (TaskCompletionSource<RunSummary>)state!;
                        pendingTcs.TrySetException(new InvalidOperationException(
                            "HeadlessUnitTestSession was cancelled before this test completed. The " +
                            "dispatch task is likely alive-but-stuck (PushFrame waiting for " +
                            "frame.Continue=false that never fires, or RunLoop blocked in ExecuteJob " +
                            "on a re-entrant dispatcher call). " +
                            "See https://github.com/JoshuaRowePhantom/Phantom.Workspaces/issues/660"));
                    },
                    tcs,
                    useSynchronizationContext: false);
            }
        }

        return await tcs.Task;
    }

    private RunSummary RunInnerWithCleanup(
        ExplicitOption explicitOption,
        IMessageBus messageBus,
        object?[] constructorArguments,
        ExceptionAggregator aggregator,
        CancellationTokenSource cancellationTokenSource)
    {
        var app = Application.Current;
        var canRestoreThemeVariant = false;
        Avalonia.Styling.ThemeVariant? requestedThemeVariant = null;
        if (app is not null)
        {
            try
            {
                requestedThemeVariant = app.RequestedThemeVariant;
                canRestoreThemeVariant = true;
            }
            catch (InvalidOperationException)
            {
                app = null;
            }
        }

        try
        {
            var task = ((ISelfExecutingXunitTestCase)_inner).Run(
                explicitOption, messageBus, constructorArguments, aggregator, cancellationTokenSource);
            return task.AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            if (canRestoreThemeVariant && app is not null)
            {
                app.RequestedThemeVariant = requestedThemeVariant;
            }

            // Force Gen2 GC after application.Dispose() has released the visual tree,
            // preventing catastrophic allocations from cascading into the next test.
            // This must run on the scheduled STA before the next Avalonia test starts.
            ForceGen2Collection();
        }
    }

    private static void ForceGen2Collection()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
    }

    // IXunitTestCase — all members delegated to inner
    Type[]? IXunitTestCase.SkipExceptions => _inner.SkipExceptions;
    string? IXunitTestCase.SkipReason => _inner.SkipReason;
    Type? IXunitTestCase.SkipType => _inner.SkipType;
    string? IXunitTestCase.SkipUnless => _inner.SkipUnless;
    string? IXunitTestCase.SkipWhen => _inner.SkipWhen;
    IXunitTestClass IXunitTestCase.TestClass => _inner.TestClass;
    int IXunitTestCase.TestClassMetadataToken => _inner.TestClassMetadataToken;
    string IXunitTestCase.TestClassName => _inner.TestClassName;
    string IXunitTestCase.TestClassSimpleName => _inner.TestClassSimpleName;
    IXunitTestCollection IXunitTestCase.TestCollection => _inner.TestCollection;
    IXunitTestMethod IXunitTestCase.TestMethod => _inner.TestMethod;
    int IXunitTestCase.TestMethodMetadataToken => _inner.TestMethodMetadataToken;
    string IXunitTestCase.TestMethodName => _inner.TestMethodName;
    string[] IXunitTestCase.TestMethodParameterTypesVSTest => _inner.TestMethodParameterTypesVSTest;
    string IXunitTestCase.TestMethodReturnTypeVSTest => _inner.TestMethodReturnTypeVSTest;
    int IXunitTestCase.Timeout => _inner.Timeout;
    ValueTask<IReadOnlyCollection<IXunitTest>> IXunitTestCase.CreateTests() => _inner.CreateTests();
    void IXunitTestCase.PostInvoke() => _inner.PostInvoke();
    void IXunitTestCase.PreInvoke() => _inner.PreInvoke();

    // ITestCase — explicit impls for base-interface members hidden by IXunitTestCase
    ITestClass? ITestCase.TestClass => _inner.TestClass;
    ITestCollection ITestCase.TestCollection => _inner.TestCollection;
    ITestMethod? ITestCase.TestMethod => _inner.TestMethod;

    // ITestCaseMetadata — explicit impls for members that IXunitTestCase overrides with narrower types
    bool ITestCaseMetadata.Explicit => _inner.Explicit;
    string? ITestCaseMetadata.SkipReason => _inner.SkipReason;
    string? ITestCaseMetadata.SourceFilePath => _inner.SourceFilePath;
    int? ITestCaseMetadata.SourceLineNumber => _inner.SourceLineNumber;
    string ITestCaseMetadata.TestCaseDisplayName => _inner.TestCaseDisplayName;
    int? ITestCaseMetadata.TestClassMetadataToken => _inner.TestClassMetadataToken;
    string? ITestCaseMetadata.TestClassName => _inner.TestClassName;
    string? ITestCaseMetadata.TestClassNamespace => _inner.TestClassNamespace;
    string? ITestCaseMetadata.TestClassSimpleName => _inner.TestClassSimpleName;
    int? ITestCaseMetadata.TestMethodArity => _inner.TestMethodArity;
    int? ITestCaseMetadata.TestMethodMetadataToken => _inner.TestMethodMetadataToken;
    string? ITestCaseMetadata.TestMethodName => _inner.TestMethodName;
    string[]? ITestCaseMetadata.TestMethodParameterTypesVSTest => _inner.TestMethodParameterTypesVSTest;
    string? ITestCaseMetadata.TestMethodReturnTypeVSTest => _inner.TestMethodReturnTypeVSTest;
    IReadOnlyDictionary<string, IReadOnlyCollection<string>> ITestCaseMetadata.Traits => _inner.Traits;
    string ITestCaseMetadata.UniqueID => _inner.UniqueID;

    // IAsyncDisposable
    ValueTask IAsyncDisposable.DisposeAsync() =>
        _inner is IAsyncDisposable d ? d.DisposeAsync() : ValueTask.CompletedTask;
}

/// <summary>
/// STA thread-based TaskScheduler for running Avalonia tests. Ensures all tests in an assembly
/// run on the same STA thread to avoid cross-thread Dispatcher access errors with shared-app isolation.
/// </summary>
internal sealed class StaTaskScheduler : TaskScheduler, IDisposable
{
    private readonly BlockingCollection<Task> _tasks = new();
    private readonly Thread _thread;

    public StaTaskScheduler()
    {
        _thread = new Thread(ThreadProc)
        {
            IsBackground = true,
            Name = "Avalonia Test STA Thread"
        };
        if (OperatingSystem.IsWindows())
        {
            _thread.SetApartmentState(ApartmentState.STA);
        }
        _thread.Start();
    }

    private void ThreadProc()
    {
        foreach (var task in _tasks.GetConsumingEnumerable())
        {
            TryExecuteTask(task);
        }
    }

    protected override void QueueTask(Task task)
    {
        _tasks.Add(task);
    }

    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
    {
        return Thread.CurrentThread == _thread && TryExecuteTask(task);
    }

    protected override IEnumerable<Task>? GetScheduledTasks()
    {
        return _tasks.ToArray();
    }

    public void Dispose()
    {
        _tasks.CompleteAdding();
    }
}

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Sdk;
using Xunit.v3;

namespace Phantom.Workspaces.Tests;

/// <summary>
/// Tests that verify PhantomAvaloniaTestCase does not block thread pool threads.
/// </summary>
public sealed class PhantomAvaloniaFactTests
{
    [Fact]
    public async Task PhantomAvaloniaTestCase_RunDoesNotBlockThreadPoolThread()
    {
        // Arrange: a fake inner test case that captures IsThreadPoolThread at the blocking point
        bool? isThreadPoolThreadDuringBlock = null;
        var innerCase = new FakeSelfExecutingXunitTestCase(captureIsThreadPoolThread: value =>
        {
            isThreadPoolThreadDuringBlock = value;
        });

        var testCase = new PhantomAvaloniaTestCase(innerCase);

        // Act: run the test case; the blocking .GetAwaiter().GetResult() in Run happens on the
        // dedicated thread, NOT on the calling thread (a thread pool thread in production).
        var summary = await ((ISelfExecutingXunitTestCase)testCase).Run(
            ExplicitOption.Off,
            new SpyMessageBus(),
            [],
            new ExceptionAggregator(),
            new CancellationTokenSource());

        // Assert
        Assert.NotNull(isThreadPoolThreadDuringBlock);
        Assert.False(isThreadPoolThreadDuringBlock,
            "PhantomAvaloniaTestCase.Run must block on a dedicated non-thread-pool thread, " +
            "not on a thread pool thread, to avoid starving the pool.");
        Assert.Equal(1, summary.Total);
    }

    [Fact]
    public async Task PhantomAvaloniaTestCase_RunPropagatesExceptionFromInnerTestCase()
    {
        // Arrange: a fake inner test case that throws on Run
        var innerCase = new FakeThrowingXunitTestCase(new InvalidOperationException("inner error"));
        var testCase = new PhantomAvaloniaTestCase(innerCase);

        // Act & Assert: the exception should propagate out of Run
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ((ISelfExecutingXunitTestCase)testCase).Run(
                ExplicitOption.Off,
                new SpyMessageBus(),
                [],
                new ExceptionAggregator(),
                new CancellationTokenSource()));
    }

    [PhantomAvaloniaFact]
    public async Task PhantomAvaloniaFact_ThreadPoolWorkItemsCompleteWithoutDeadlock()
    {
        // This test verifies that thread pool threads are not exhausted while an Avalonia test runs.
        // If PhantomAvaloniaTestCase.Run were blocking a thread pool thread, scheduling new work
        // items via Task.Factory.StartNew would deadlock (or time out) under load.
        const int taskCount = 32;
        var tasks = new Task<int>[taskCount];
        for (int i = 0; i < taskCount; i++)
        {
            int captured = i;
            tasks[i] = Task.Factory.StartNew(
                () => captured,
                CancellationToken.None,
                TaskCreationOptions.None,
                TaskScheduler.Default);
        }

        var results = await Task.WhenAll(tasks);

        Assert.Equal(taskCount, results.Length);
        for (int i = 0; i < taskCount; i++)
        {
            Assert.Equal(i, results[i]);
        }
    }

    private sealed class FakeSelfExecutingXunitTestCase : ISelfExecutingXunitTestCase
    {
        private readonly Action<bool> _captureIsThreadPoolThread;

        public FakeSelfExecutingXunitTestCase(Action<bool> captureIsThreadPoolThread)
        {
            _captureIsThreadPoolThread = captureIsThreadPoolThread;
        }

        public ValueTask<RunSummary> Run(
            ExplicitOption explicitOption,
            IMessageBus messageBus,
            object?[] constructorArguments,
            ExceptionAggregator aggregator,
            CancellationTokenSource cancellationTokenSource)
        {
            // Simulate what AvaloniaTestCase.Run does: blocks calling thread intentionally.
            // We capture IsThreadPoolThread here to verify it is called on a dedicated thread.
            _captureIsThreadPoolThread(Thread.CurrentThread.IsThreadPoolThread);
            return ValueTask.FromResult(new RunSummary { Total = 1 });
        }

        // Minimal IXunitTestCase stubs
        Type[]? IXunitTestCase.SkipExceptions => null;
        string? IXunitTestCase.SkipReason => null;
        Type? IXunitTestCase.SkipType => null;
        string? IXunitTestCase.SkipUnless => null;
        string? IXunitTestCase.SkipWhen => null;
        IXunitTestClass IXunitTestCase.TestClass => null!;
        int IXunitTestCase.TestClassMetadataToken => 0;
        string IXunitTestCase.TestClassName => "Fake";
        string IXunitTestCase.TestClassSimpleName => "Fake";
        IXunitTestCollection IXunitTestCase.TestCollection => null!;
        IXunitTestMethod IXunitTestCase.TestMethod => null!;
        int IXunitTestCase.TestMethodMetadataToken => 0;
        string IXunitTestCase.TestMethodName => "FakeMethod";
        string[] IXunitTestCase.TestMethodParameterTypesVSTest => [];
        string IXunitTestCase.TestMethodReturnTypeVSTest => "System.Void";
        int IXunitTestCase.Timeout => 0;
        ValueTask<IReadOnlyCollection<IXunitTest>> IXunitTestCase.CreateTests() => ValueTask.FromResult<IReadOnlyCollection<IXunitTest>>([]);
        void IXunitTestCase.PostInvoke() { }
        void IXunitTestCase.PreInvoke() { }

        // ITestCase
        ITestClass? ITestCase.TestClass => null;
        ITestCollection ITestCase.TestCollection => null!;
        ITestMethod? ITestCase.TestMethod => null;

        // ITestCaseMetadata
        bool ITestCaseMetadata.Explicit => false;
        string? ITestCaseMetadata.SkipReason => null;
        string? ITestCaseMetadata.SourceFilePath => null;
        int? ITestCaseMetadata.SourceLineNumber => null;
        string ITestCaseMetadata.TestCaseDisplayName => "Fake.FakeMethod";
        int? ITestCaseMetadata.TestClassMetadataToken => 0;
        string? ITestCaseMetadata.TestClassName => "Fake";
        string? ITestCaseMetadata.TestClassNamespace => null;
        string? ITestCaseMetadata.TestClassSimpleName => "Fake";
        int? ITestCaseMetadata.TestMethodArity => 0;
        int? ITestCaseMetadata.TestMethodMetadataToken => 0;
        string? ITestCaseMetadata.TestMethodName => "FakeMethod";
        string[]? ITestCaseMetadata.TestMethodParameterTypesVSTest => [];
        string? ITestCaseMetadata.TestMethodReturnTypeVSTest => "System.Void";
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> ITestCaseMetadata.Traits =>
            new Dictionary<string, IReadOnlyCollection<string>>();
        string ITestCaseMetadata.UniqueID => "fake-unique-id";
    }

    private sealed class FakeThrowingXunitTestCase(Exception exceptionToThrow) : ISelfExecutingXunitTestCase
    {
        public ValueTask<RunSummary> Run(
            ExplicitOption explicitOption,
            IMessageBus messageBus,
            object?[] constructorArguments,
            ExceptionAggregator aggregator,
            CancellationTokenSource cancellationTokenSource)
            => throw exceptionToThrow;

        // Minimal stubs
        Type[]? IXunitTestCase.SkipExceptions => null;
        string? IXunitTestCase.SkipReason => null;
        Type? IXunitTestCase.SkipType => null;
        string? IXunitTestCase.SkipUnless => null;
        string? IXunitTestCase.SkipWhen => null;
        IXunitTestClass IXunitTestCase.TestClass => null!;
        int IXunitTestCase.TestClassMetadataToken => 0;
        string IXunitTestCase.TestClassName => "Fake";
        string IXunitTestCase.TestClassSimpleName => "Fake";
        IXunitTestCollection IXunitTestCase.TestCollection => null!;
        IXunitTestMethod IXunitTestCase.TestMethod => null!;
        int IXunitTestCase.TestMethodMetadataToken => 0;
        string IXunitTestCase.TestMethodName => "FakeThrowMethod";
        string[] IXunitTestCase.TestMethodParameterTypesVSTest => [];
        string IXunitTestCase.TestMethodReturnTypeVSTest => "System.Void";
        int IXunitTestCase.Timeout => 0;
        ValueTask<IReadOnlyCollection<IXunitTest>> IXunitTestCase.CreateTests() => ValueTask.FromResult<IReadOnlyCollection<IXunitTest>>([]);
        void IXunitTestCase.PostInvoke() { }
        void IXunitTestCase.PreInvoke() { }
        ITestClass? ITestCase.TestClass => null;
        ITestCollection ITestCase.TestCollection => null!;
        ITestMethod? ITestCase.TestMethod => null;
        bool ITestCaseMetadata.Explicit => false;
        string? ITestCaseMetadata.SkipReason => null;
        string? ITestCaseMetadata.SourceFilePath => null;
        int? ITestCaseMetadata.SourceLineNumber => null;
        string ITestCaseMetadata.TestCaseDisplayName => "Fake.FakeThrowMethod";
        int? ITestCaseMetadata.TestClassMetadataToken => 0;
        string? ITestCaseMetadata.TestClassName => "Fake";
        string? ITestCaseMetadata.TestClassNamespace => null;
        string? ITestCaseMetadata.TestClassSimpleName => "Fake";
        int? ITestCaseMetadata.TestMethodArity => 0;
        int? ITestCaseMetadata.TestMethodMetadataToken => 0;
        string? ITestCaseMetadata.TestMethodName => "FakeThrowMethod";
        string[]? ITestCaseMetadata.TestMethodParameterTypesVSTest => [];
        string? ITestCaseMetadata.TestMethodReturnTypeVSTest => "System.Void";
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> ITestCaseMetadata.Traits =>
            new Dictionary<string, IReadOnlyCollection<string>>();
        string ITestCaseMetadata.UniqueID => "fake-throwing-unique-id";
    }

    private sealed class SpyMessageBus : IMessageBus
    {
        public bool QueueMessage(IMessageSinkMessage message) => true;
        public void Dispose() { }
    }
}

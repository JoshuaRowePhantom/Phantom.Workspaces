using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Xunit;
using Xunit.Sdk;
using Xunit.v3;

namespace Phantom.Workspaces.Gui.Shared.Tests;

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

internal sealed class PhantomAvaloniaTestCase(IXunitTestCase inner)
    : ISelfExecutingXunitTestCase, IAsyncDisposable
{
    public async ValueTask<RunSummary> Run(
        ExplicitOption explicitOption,
        IMessageBus messageBus,
        object?[] constructorArguments,
        ExceptionAggregator aggregator,
        CancellationTokenSource cancellationTokenSource)
    {
        var summary = await ((ISelfExecutingXunitTestCase)inner).Run(
            explicitOption, messageBus, constructorArguments, aggregator, cancellationTokenSource);

        // Force Gen2 GC after application.Dispose() has released the visual tree,
        // preventing catastrophic allocations from cascading into the next test.
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);

        return summary;
    }

    // IXunitTestCase — all members delegated to inner
    Type[]? IXunitTestCase.SkipExceptions => inner.SkipExceptions;
    string? IXunitTestCase.SkipReason => inner.SkipReason;
    Type? IXunitTestCase.SkipType => inner.SkipType;
    string? IXunitTestCase.SkipUnless => inner.SkipUnless;
    string? IXunitTestCase.SkipWhen => inner.SkipWhen;
    IXunitTestClass IXunitTestCase.TestClass => inner.TestClass;
    int IXunitTestCase.TestClassMetadataToken => inner.TestClassMetadataToken;
    string IXunitTestCase.TestClassName => inner.TestClassName;
    string IXunitTestCase.TestClassSimpleName => inner.TestClassSimpleName;
    IXunitTestCollection IXunitTestCase.TestCollection => inner.TestCollection;
    IXunitTestMethod IXunitTestCase.TestMethod => inner.TestMethod;
    int IXunitTestCase.TestMethodMetadataToken => inner.TestMethodMetadataToken;
    string IXunitTestCase.TestMethodName => inner.TestMethodName;
    string[] IXunitTestCase.TestMethodParameterTypesVSTest => inner.TestMethodParameterTypesVSTest;
    string IXunitTestCase.TestMethodReturnTypeVSTest => inner.TestMethodReturnTypeVSTest;
    int IXunitTestCase.Timeout => inner.Timeout;
    ValueTask<IReadOnlyCollection<IXunitTest>> IXunitTestCase.CreateTests() => inner.CreateTests();
    void IXunitTestCase.PostInvoke() => inner.PostInvoke();
    void IXunitTestCase.PreInvoke() => inner.PreInvoke();

    // ITestCase — explicit impls for base-interface members hidden by IXunitTestCase
    ITestClass? ITestCase.TestClass => inner.TestClass;
    ITestCollection ITestCase.TestCollection => inner.TestCollection;
    ITestMethod? ITestCase.TestMethod => inner.TestMethod;

    // ITestCaseMetadata — explicit impls for members that IXunitTestCase overrides with narrower types
    bool ITestCaseMetadata.Explicit => inner.Explicit;
    string? ITestCaseMetadata.SkipReason => inner.SkipReason;
    string? ITestCaseMetadata.SourceFilePath => inner.SourceFilePath;
    int? ITestCaseMetadata.SourceLineNumber => inner.SourceLineNumber;
    string ITestCaseMetadata.TestCaseDisplayName => inner.TestCaseDisplayName;
    int? ITestCaseMetadata.TestClassMetadataToken => inner.TestClassMetadataToken;
    string? ITestCaseMetadata.TestClassName => inner.TestClassName;
    string? ITestCaseMetadata.TestClassNamespace => inner.TestClassNamespace;
    string? ITestCaseMetadata.TestClassSimpleName => inner.TestClassSimpleName;
    int? ITestCaseMetadata.TestMethodArity => inner.TestMethodArity;
    int? ITestCaseMetadata.TestMethodMetadataToken => inner.TestMethodMetadataToken;
    string? ITestCaseMetadata.TestMethodName => inner.TestMethodName;
    string[]? ITestCaseMetadata.TestMethodParameterTypesVSTest => inner.TestMethodParameterTypesVSTest;
    string? ITestCaseMetadata.TestMethodReturnTypeVSTest => inner.TestMethodReturnTypeVSTest;
    IReadOnlyDictionary<string, IReadOnlyCollection<string>> ITestCaseMetadata.Traits => inner.Traits;
    string ITestCaseMetadata.UniqueID => inner.UniqueID;

    // IAsyncDisposable
    ValueTask IAsyncDisposable.DisposeAsync() =>
        inner is IAsyncDisposable d ? d.DisposeAsync() : ValueTask.CompletedTask;
}

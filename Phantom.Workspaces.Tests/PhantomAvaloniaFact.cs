using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Xunit;
using Xunit.Sdk;
using Xunit.v3;

namespace Phantom.Workspaces.Tests;

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

internal sealed class PhantomAvaloniaTestCase : ISelfExecutingXunitTestCase, IXunitSerializable, IAsyncDisposable
{
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
        var summary = await ((ISelfExecutingXunitTestCase)_inner).Run(
            explicitOption, messageBus, constructorArguments, aggregator, cancellationTokenSource);

        // Force Gen2 GC after application.Dispose() has released the visual tree,
        // preventing catastrophic allocations from cascading into the next test.
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);

        return summary;
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

using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.Tests.Access;

public sealed class AccessContractTests
{
    private sealed class TestResult : ICredentialAccessResult
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record TestRequest : CredentialAccessRequest<TestResult>;

    [Fact]
    public void AccessRequest_ConcreteType_BindsDeclaredResultType()
    {
        Assert.True(typeof(AccessRequest<TestResult>).IsAssignableFrom(typeof(TestRequest)));
        Assert.Equal(typeof(TestResult), typeof(TestRequest).BaseType!.GenericTypeArguments.Single());
        Assert.Equal(typeof(ICredentialAccessResult),
            typeof(CredentialAccessRequest<>).GetGenericArguments().Single().GetGenericParameterConstraints().Single());
    }

    [Fact]
    public void IAccessBroker_AcquireAsync_HasCancellationAndDisposableResultConstraint()
    {
        var method = Assert.Single(typeof(IAccessBroker).GetMethods());
        Assert.Equal("AcquireAsync", method.Name);
        Assert.Equal(typeof(CancellationToken), method.GetParameters()[1].ParameterType);
        Assert.True(method.GetParameters()[1].HasDefaultValue);
        Assert.Contains(typeof(IAccessResult), method.GetGenericArguments().Single().GetGenericParameterConstraints());
        Assert.True(typeof(IAsyncDisposable).IsAssignableFrom(typeof(IAccessBroker)));
    }
}

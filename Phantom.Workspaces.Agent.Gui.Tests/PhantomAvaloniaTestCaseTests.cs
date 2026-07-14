using System.Threading;

using Moq;
using Phantom.Workspaces.Testing.Gui;
using Xunit.Sdk;
using Xunit.v3;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class PhantomAvaloniaTestCaseTests
{
    [Fact]
    public async Task PhantomAvaloniaFact_ForcesGen2GCAfterTest()
    {
        var gen2Before = GC.CollectionCount(2);

        var mockInner = new Mock<ISelfExecutingXunitTestCase>();
        mockInner
            .Setup(x => x.Run(
                It.IsAny<ExplicitOption>(),
                It.IsAny<IMessageBus>(),
                It.IsAny<object[]>(),
                It.IsAny<ExceptionAggregator>(),
                It.IsAny<CancellationTokenSource>()))
            .ReturnsAsync(new RunSummary { Total = 1 });

        var sut = new PhantomAvaloniaTestCase(mockInner.Object);
        await sut.Run(
            ExplicitOption.Off,
            Mock.Of<IMessageBus>(),
            [],
            new ExceptionAggregator(),
            new CancellationTokenSource());

        Assert.True(GC.CollectionCount(2) > gen2Before, "Gen2 GC should have been forced after the test ran.");
    }
}

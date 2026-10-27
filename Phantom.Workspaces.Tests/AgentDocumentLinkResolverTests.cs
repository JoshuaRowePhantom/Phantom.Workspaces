using Phantom.Workspaces.ViewModels;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class AgentDocumentLinkResolverTests
{
    [Theory]
    [InlineData("report.md", "file:///C:/work/agent/report.md")]
    [InlineData("../report.md", "file:///C:/work/report.md")]
    [InlineData("./a%20b%23%25.md#section", "file:///C:/work/agent/a%20b%23%25.md#section")]
    [InlineData("C:\\other\\report.md", "file:///C:/other/report.md")]
    [InlineData("\\\\server\\share\\report.md", "file://server/share/report.md")]
    [InlineData("https://example.com/report.md", "https://example.com/report.md")]
    [InlineData("mailto:person@example.com", "mailto:person@example.com")]
    public void Resolve_SupportedReference_ProducesAbsoluteBrowserSource(string reference, string expected)
    {
        var destination = AgentDocumentLinkResolver.Resolve(reference, @"C:\work\agent", true);
        Assert.Equal(expected, destination.AbsoluteUri);
        var browser = new WebViewModel(destination.AbsoluteUri, null!) { Id = "document", Title = "Document" };
        Assert.Equal(destination, browser.SourceUri);
        Assert.Equal(destination.AbsoluteUri, browser.AddressBarUrl);
        Assert.Equal(destination.AbsoluteUri, browser.HomeUrl);
    }

    [Theory]
    [InlineData("report.md", null, true)]
    [InlineData("report.md", "relative", true)]
    [InlineData("report.md", "C:\\work", false)]
    [InlineData("file:///C:/work/report.md", "C:\\work", false)]
    [InlineData("javascript:alert(1)", "C:\\work", true)]
    public void Resolve_UnsupportedOrUnownedReference_FailsVisibly(
        string reference,
        string? directory,
        bool isLocal)
        => Assert.Throws<InvalidOperationException>(() => AgentDocumentLinkResolver.Resolve(reference, directory, isLocal));
}

using Avalonia.Controls.Documents;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class AgentChatFlowDocumentBuilderTests
{
    [AvaloniaFact]
    public void CreateDocument_ReturnsEmptyFlowDocument()
    {
        var document = AgentChatFlowDocumentBuilder.CreateDocument();
        Assert.NotNull(document);
        Assert.Empty(document.Blocks);
    }

    // Avalonia bug: RichTextElementCollection<T>.Clear() throws NullReferenceException when
    // EnsureTextDocument() has been called. Use DocumentBlockUtilities.ClearBlocks() instead.
    [AvaloniaFact]
    public void ClearBlocks_AfterEnsuringTextDocument_DoesNotThrow()
    {
        var document = AgentChatFlowDocumentBuilder.CreateDocument();
        var section = new Section();
        section.Blocks.Add(new Paragraph(new RichRun("hello")));
        document.Blocks.Add(section);
        _ = document.EnsureTextDocument();

        DocumentBlockUtilities.ClearBlocks(section);

        Assert.Empty(section.Blocks);
    }
}

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
        var document = new FlowDocument();
        var section = new Section();
        section.Blocks.Add(new Paragraph(new RichRun("hello")));
        document.Blocks.Add(section);
        _ = document.EnsureTextDocument();

        DocumentBlockUtilities.ClearBlocks(section);

        Assert.Empty(section.Blocks);
    }

    [AvaloniaFact]
    public void RemoveNestedSection_AfterEnsuringTextDocument_KeepsParentSectionInDocument()
    {
        var document = new FlowDocument();
        var sectionA = new Section();
        var sectionB = new Section();
        sectionB.Blocks.Add(new Paragraph(new RichRun("nested")));
        sectionA.Blocks.Add(sectionB);
        document.Blocks.Add(sectionA);
        sectionA.Blocks.RemoveAt(0);
        _ = document.EnsureTextDocument();

        Assert.Single(document.Blocks);
        Assert.Same(sectionA, document.Blocks[0]);
        Assert.Empty(sectionA.Blocks);

        document.InitializeTextDocument();
    }
}

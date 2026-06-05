using Avalonia.Controls.Documents;

namespace Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;

/// <summary>
/// Abstract base class for document models that render to a Block in a FlowDocument.
/// </summary>
internal abstract class AgentChatDocumentBlockModel
{
    /// <summary>
    /// The Block that represents this model's rendered content in the FlowDocument.
    /// </summary>
    public abstract Block Block { get; }
}


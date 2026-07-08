using System.Windows.Input;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

/// <summary>
/// Represents a single diagnostic <see cref="Microsoft.Extensions.AI.AIContent"/> block
/// with an inspect affordance that opens <see cref="Controls.AIContentInspectorWindow"/>.
/// </summary>
public sealed class DiagnosticItemViewModel : ViewModelBase
{
    private readonly Action inspectCallback;

    public DiagnosticItemViewModel(string contentId, string contentJson, Action inspectCallback)
    {
        this.ContentId = contentId;
        this.ContentJson = contentJson;
        this.inspectCallback = inspectCallback;
        this.InspectCommand = new RelayCommand(this.Inspect);
    }

    public string ContentId { get; }

    public string ContentJson { get; }

    public ICommand InspectCommand { get; }

    private void Inspect() => this.inspectCallback();
}

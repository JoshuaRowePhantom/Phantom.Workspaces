namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class ChatHistoryToolContentViewModel : ViewModelBase
{
    public ChatHistoryToolContentViewModel(
        string kindLabel,
        string toolName,
        string rawContent,
        string? prettyJson)
    {
        this.KindLabel = kindLabel;
        this.ToolName = string.IsNullOrWhiteSpace(toolName) ? "(unknown tool)" : toolName;
        this.RawContent = rawContent;
        this.PrettyJson = prettyJson;
    }

    public string KindLabel { get; }

    public string ToolName { get; }

    public string Header => $"{this.KindLabel}: {this.ToolName}";

    public string RawContent { get; }

    public string? PrettyJson { get; }

    public bool HasPrettyJson => !string.IsNullOrWhiteSpace(this.PrettyJson);

    public bool HasRawOnly => !this.HasPrettyJson;
}

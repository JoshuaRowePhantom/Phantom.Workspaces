using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class ChatHistoryItemViewModel : ViewModelBase
{
    private string text;
    private string reasoningText;
    private bool isInProgress;
    private bool isReasoningVisible;

    public ChatHistoryItemViewModel(AgentChatHistoryItem item)
    {
        this.IsUser = item.Role == ChatRole.User;
        this.RoleLabel = this.IsUser ? "user" : "assistant";
        this.text = item.Text;
        this.reasoningText = item.ReasoningText;
        this.isInProgress = item.IsInProgress;
    }

    public bool IsUser { get; }
    public string RoleLabel { get; }

    public string Text
    {
        get => this.text;
        private set => this.SetProperty(ref this.text, value);
    }

    public bool IsInProgress
    {
        get => this.isInProgress;
        private set => this.SetProperty(ref this.isInProgress, value);
    }

    public string ReasoningText
    {
        get => this.reasoningText;
        private set
        {
            if (this.SetProperty(ref this.reasoningText, value))
            {
                this.RaisePropertyChanged(nameof(this.HasReasoningLine));
                this.RaisePropertyChanged(nameof(this.ReasoningDisplayText));
            }
        }
    }

    public bool HasReasoningLine => !this.IsUser && (this.IsInProgress || (this.isReasoningVisible && !string.IsNullOrEmpty(this.reasoningText)));

    public string ReasoningDisplayText
        => this.IsUser
            ? string.Empty
            : this.isReasoningVisible && !string.IsNullOrEmpty(this.reasoningText)
                ? this.reasoningText
                : this.IsInProgress
                    ? "Thinking ..."
                    : string.Empty;

    public void UpdateFrom(AgentChatHistoryItem item)
    {
        this.Text = item.Text;
        this.ReasoningText = item.ReasoningText;
        this.IsInProgress = item.IsInProgress;
    }

    public void SetReasoningVisible(bool visible)
    {
        if (!this.SetProperty(ref this.isReasoningVisible, visible))
        {
            return;
        }

        this.RaisePropertyChanged(nameof(this.HasReasoningLine));
        this.RaisePropertyChanged(nameof(this.ReasoningDisplayText));
    }
}

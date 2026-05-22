using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class RunningItemViewModel : ViewModelBase
{
    private string text;

    public RunningItemViewModel(AgentChatRunningItem source)
    {
        this.Source = source;
        this.text = source.CurrentText;
    }

    internal AgentChatRunningItem Source { get; }

    public string Text
    {
        get => this.text;
        private set => this.SetProperty(ref this.text, value);
    }

    internal void UpdateText() => this.Text = this.Source.CurrentText;
}

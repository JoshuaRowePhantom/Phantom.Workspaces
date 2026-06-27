using System.ComponentModel;
using System.Globalization;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class AgentChatStatusLineViewModel : ViewModelBase, IDisposable
{
    private readonly AgentViewModel agent;
    private bool isThinking;
    private string modelDisplay = "(none)";
    private string providerDisplay = "(none)";
    private string? tokensDisplay;

    public AgentChatStatusLineViewModel(AgentViewModel agent)
    {
        this.agent = agent;
        this.UpdateFromAgent();
        this.agent.PropertyChanged += this.OnAgentPropertyChanged;
    }

    public bool IsThinking
    {
        get => this.isThinking;
        private set
        {
            if (this.SetProperty(ref this.isThinking, value))
            {
                this.RaisePropertyChanged(nameof(this.HasVisibleContent));
            }
        }
    }

    public string ModelDisplay
    {
        get => this.modelDisplay;
        private set
        {
            if (this.SetProperty(ref this.modelDisplay, value))
            {
                this.RaisePropertyChanged(nameof(this.HasModel));
                this.RaisePropertyChanged(nameof(this.HasVisibleContent));
            }
        }
    }

    public string ProviderDisplay
    {
        get => this.providerDisplay;
        private set
        {
            if (this.SetProperty(ref this.providerDisplay, value))
            {
                this.RaisePropertyChanged(nameof(this.HasProvider));
                this.RaisePropertyChanged(nameof(this.HasVisibleContent));
            }
        }
    }

    public string? TokensDisplay
    {
        get => this.tokensDisplay;
        private set
        {
            if (this.SetProperty(ref this.tokensDisplay, value))
            {
                this.RaisePropertyChanged(nameof(this.HasTokens));
                this.RaisePropertyChanged(nameof(this.HasVisibleContent));
            }
        }
    }

    public bool HasTokens => !string.IsNullOrEmpty(this.TokensDisplay);

    public bool HasProvider => !string.IsNullOrEmpty(this.ProviderDisplay);

    public bool HasModel => !string.IsNullOrEmpty(this.ModelDisplay);

    public bool HasVisibleContent => this.IsThinking || this.HasModel || this.HasProvider || this.HasTokens;

    public bool IsReasoningVisible => this.agent.IsReasoningVisible;

    public void Dispose()
    {
        this.agent.PropertyChanged -= this.OnAgentPropertyChanged;
    }

    private static string CreateDisplayText(string value)
        => string.IsNullOrWhiteSpace(value) ? "(none)" : value;

    private static string? CreateTokensDisplay(long? inputTokenCount, long? outputTokenCount)
        => inputTokenCount.HasValue && outputTokenCount.HasValue
            ? string.Format(
                CultureInfo.InvariantCulture,
                "{0:N0} in / {1:N0} out",
                inputTokenCount.Value,
                outputTokenCount.Value)
            : null;

    private void UpdateFromAgent()
    {
        this.IsThinking = this.agent.IsChatRunning;
        this.ModelDisplay = CreateDisplayText(this.agent.ModelId);
        this.ProviderDisplay = CreateDisplayText(this.agent.ModelProvider);
        this.TokensDisplay = CreateTokensDisplay(
            this.agent.TotalInputTokenCount,
            this.agent.TotalOutputTokenCount);
    }

    private void OnAgentPropertyChanged(object? sender, PropertyChangedEventArgs propertyChangedEvent)
    {
        if (string.Equals(propertyChangedEvent.PropertyName, nameof(AgentViewModel.IsChatRunning), StringComparison.Ordinal))
        {
            this.IsThinking = this.agent.IsChatRunning;
        }
        else if (string.Equals(propertyChangedEvent.PropertyName, nameof(AgentViewModel.ModelId), StringComparison.Ordinal))
        {
            this.ModelDisplay = CreateDisplayText(this.agent.ModelId);
        }
        else if (string.Equals(propertyChangedEvent.PropertyName, nameof(AgentViewModel.ModelProvider), StringComparison.Ordinal))
        {
            this.ProviderDisplay = CreateDisplayText(this.agent.ModelProvider);
        }
        else if (string.Equals(propertyChangedEvent.PropertyName, nameof(AgentViewModel.TotalInputTokenCount), StringComparison.Ordinal)
            || string.Equals(propertyChangedEvent.PropertyName, nameof(AgentViewModel.TotalOutputTokenCount), StringComparison.Ordinal))
        {
            this.TokensDisplay = CreateTokensDisplay(
                this.agent.TotalInputTokenCount,
                this.agent.TotalOutputTokenCount);
        }
        else if (string.Equals(propertyChangedEvent.PropertyName, nameof(AgentViewModel.IsReasoningVisible), StringComparison.Ordinal))
        {
            this.RaisePropertyChanged(nameof(this.IsReasoningVisible));
        }
    }
}

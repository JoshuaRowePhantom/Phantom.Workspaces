using System.ComponentModel;
using System.Globalization;
using System.Text;
using Phantom.Workspaces.Agent.Gui.ViewModels.Visualization;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class AgentChatStatusLineViewModel : ViewModelBase, IDisposable, IAgentStatusSink
{
    private readonly AgentViewModel agent;
    private bool isThinking;
    private string modelDisplay = "(none)";
    private string providerDisplay = "(none)";
    private string? tokensDisplay;
    private string? tokensTooltip;
    private string? intentDisplay;

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
                if (!value)
                {
                    this.IntentDisplay = null;
                }

                this.RaisePropertyChanged(nameof(this.HasVisibleContent));
            }
        }
    }

    /// <summary>
    /// Short description of what the agent is currently doing, populated from
    /// <see cref="StatusUpdate"/> results and cleared when <see cref="IsThinking"/> transitions to
    /// false. Shown in a muted/italic style between the brain icon and model/provider fields.
    /// </summary>
    public string? IntentDisplay
    {
        get => this.intentDisplay;
        private set => this.SetProperty(ref this.intentDisplay, value);
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

    /// <summary>
    /// Per-component usage breakdown shown as a tooltip over the token footer: input (with cached /
    /// non-cached split), reasoning, output and dollar cost. <c>null</c> when there is no token data.
    /// </summary>
    public string? TokensTooltip
    {
        get => this.tokensTooltip;
        private set
        {
            if (this.SetProperty(ref this.tokensTooltip, value))
            {
                this.RaisePropertyChanged(nameof(this.HasTokensTooltip));
            }
        }
    }

    public bool HasTokensTooltip => !string.IsNullOrEmpty(this.TokensTooltip);

    public bool HasProvider => !string.IsNullOrEmpty(this.ProviderDisplay);

    public bool HasModel => !string.IsNullOrEmpty(this.ModelDisplay);

    public bool HasVisibleContent => this.IsThinking || this.HasModel || this.HasProvider || this.HasTokens;

    public bool IsReasoningVisible => this.agent.IsReasoningVisible;

    public string ReasoningIndicatorText =>
        this.agent.IsReasoningVisible ? "🧠 Showing Reasoning" : "🚫🧠 Not Showing Reasoning";

    public void Dispose()
    {
        this.agent.PropertyChanged -= this.OnAgentPropertyChanged;
    }

    public void UpdateStatus(AgentStatusField field, string? value)
    {
        if (field == AgentStatusField.Intent)
        {
            this.IntentDisplay = string.IsNullOrEmpty(value) ? null : value;
        }
    }

    private static string CreateDisplayText(string value)
        => string.IsNullOrWhiteSpace(value) ? "(none)" : value;

    private static string? CreateTokensDisplay(
        long? inputTokenCount,
        long? outputTokenCount,
        long? cacheReadTokenCount,
        double? sessionCostUsd)
    {
        if (inputTokenCount is not long input || outputTokenCount is not long output)
        {
            return null;
        }

        var builder = new StringBuilder();
        builder.AppendFormat(CultureInfo.InvariantCulture, "{0:N0} in", input);
        if (cacheReadTokenCount is long cacheRead && cacheRead > 0)
        {
            builder.AppendFormat(CultureInfo.InvariantCulture, " ({0:N0} cached)", cacheRead);
        }

        builder.AppendFormat(CultureInfo.InvariantCulture, " / {0:N0} out", output);
        if (sessionCostUsd is double cost && cost > 0)
        {
            builder.AppendFormat(CultureInfo.InvariantCulture, " \u00b7 ${0:N2}", cost);
        }

        return builder.ToString();
    }

    private static string? CreateTokensTooltip(
        long? inputTokenCount,
        long? outputTokenCount,
        long? cacheReadTokenCount,
        long? reasoningTokenCount,
        double? sessionCostUsd)
    {
        if (inputTokenCount is not long input || outputTokenCount is not long output)
        {
            return null;
        }

        var builder = new StringBuilder();
        builder.AppendFormat(CultureInfo.InvariantCulture, "Input: {0:N0}", input);
        if (cacheReadTokenCount is long cacheRead && cacheRead > 0)
        {
            var nonCached = System.Math.Max(0L, input - cacheRead);
            builder.AppendFormat(
                CultureInfo.InvariantCulture,
                " (cached: {0:N0}, non-cached: {1:N0})",
                cacheRead,
                nonCached);
        }

        if (reasoningTokenCount is long reasoning && reasoning > 0)
        {
            builder.AppendFormat(CultureInfo.InvariantCulture, " \u00b7 Reasoning: {0:N0}", reasoning);
        }

        builder.AppendFormat(CultureInfo.InvariantCulture, " \u00b7 Output: {0:N0}", output);
        if (sessionCostUsd is double cost && cost > 0)
        {
            builder.AppendFormat(CultureInfo.InvariantCulture, " \u00b7 Cost: ${0:N2}", cost);
        }

        return builder.ToString();
    }

    private void UpdateTokensFromAgent()
    {
        this.TokensDisplay = CreateTokensDisplay(
            this.agent.TotalInputTokenCount,
            this.agent.TotalOutputTokenCount,
            this.agent.TotalCacheReadTokenCount,
            this.agent.TotalSessionCostUsd);
        this.TokensTooltip = CreateTokensTooltip(
            this.agent.TotalInputTokenCount,
            this.agent.TotalOutputTokenCount,
            this.agent.TotalCacheReadTokenCount,
            this.agent.TotalReasoningTokenCount,
            this.agent.TotalSessionCostUsd);
    }

    private void UpdateFromAgent()
    {
        this.IsThinking = this.agent.IsChatRunning;
        this.ModelDisplay = CreateDisplayText(this.agent.ModelId);
        this.ProviderDisplay = CreateDisplayText(this.agent.ModelProvider);
        this.UpdateTokensFromAgent();
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
            || string.Equals(propertyChangedEvent.PropertyName, nameof(AgentViewModel.TotalOutputTokenCount), StringComparison.Ordinal)
            || string.Equals(propertyChangedEvent.PropertyName, nameof(AgentViewModel.TotalCacheReadTokenCount), StringComparison.Ordinal)
            || string.Equals(propertyChangedEvent.PropertyName, nameof(AgentViewModel.TotalReasoningTokenCount), StringComparison.Ordinal)
            || string.Equals(propertyChangedEvent.PropertyName, nameof(AgentViewModel.TotalSessionCostUsd), StringComparison.Ordinal))
        {
            this.UpdateTokensFromAgent();
        }
        else if (string.Equals(propertyChangedEvent.PropertyName, nameof(AgentViewModel.IsReasoningVisible), StringComparison.Ordinal))
        {
            this.RaisePropertyChanged(nameof(this.IsReasoningVisible));
            this.RaisePropertyChanged(nameof(this.ReasoningIndicatorText));
        }
    }
}

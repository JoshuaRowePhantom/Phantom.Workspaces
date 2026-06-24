# Agent Chat Status Line Design

## Purpose

Add a **status line** to the agent chat output control that displays per-message and per-session information at the bottom of the output area, below the input editor. This gives users quick insight into which model is producing responses, how much they are using, and whether the agent is currently thinking — without requiring them to open the Chat Details panel.

A **thinking emoticon** (a "brain" icon in a thematic pink color) appears alongside the status line when the agent is actively generating text (reasoning / streaming).

## Visual Design

### Status Line Layout

The status line sits at the very bottom of the agent workspace, below the input queue editor. It is a compact single row with horizontally-arranged segments:

```
[brain bar] [model: o3.1] • [provider: GitHub Models] • [credits: $0.12] • [tokens: 1,204 in / 512 out]
```

Segments from left to right:

| Segment | Source | Description |
|---|---|---|
| Thinking bar | `StatusLine.IsThinking` | 4px pink pill ProgressBar (indeterminate animation), visible only while streaming/thinking. Hidden otherwise. Replaces the old `ProgressBar.agent-chat-running-progress`. |
| Model | `AgentViewModel.ModelId` | Display name of the model that produced the last message (or currently running). Shows `(none)` when no agent is loaded. |
| Provider | `AgentViewModel.ModelProvider` | The provider in use (e.g., "GitHub Models", "Ollama", "OpenAI"). |
| Credits | `AgentChat.TotalCredits` (to be added) | Cumulative AI credits used for this session. Null/hidden when not tracked. |
| Token counts | `AgentChat.TotalTokens` (to be added) | `{input} in / {output} out`. Hidden when not tracked. |

### Removing Existing Progress Bars

The current codebase renders an indeterminate `ProgressBar` in two locations:

1. `AgentChatOutputControl.axaml` — a `.agent-chat-running-progress` bar below the selectable output text (visible when `IsChatRunning`). This is styled with `MinHeight="2"` and is essentially an invisible blue strip.
2. `RunningChatDocumentModels.cs` — a FlowDocument `BlockUIContainer` progress bar rendered inside the document's running section.

**Both are removed.** The brain ProgressBar serves as the single, visible progress indicator.

### Brain ProgressBar (replaces both old bars)

The thinking indicator is a styled indeterminate `ProgressBar` whose track fills with pink and whose animation conveys progress:

```xml
<ProgressBar
    x:Name="ThinkingBrainBar"
    Classes="agent-chat-thinking-brain"
    IsIndeterminate="True"
    IsVisible="{Binding StatusLine.IsThinking}"
    Margin="12,8,0,0" />
```

Styling in `AgentChatStatusLineStyles.axaml`:

```xml
<Style Selector="ProgressBar.agent-chat-thinking-brain">
    <Setter Property="Background" Value="#2A2A2A" />
    <Setter Property="Foreground" Value="#E86090" />
    <Setter Property="Minimum" Value="0" />
    <Setter Property="Maximum" Value="1" />
    <Setter Property="Height" Value="4" />
    <Setter Property="CornerRadius" Value="2" />
</Style>
```

- `Foreground` (the fill color) is the pink `#E86090`.
- `Background` is a neutral dark track so the bar is visible even when not animated.
- `IsIndeterminate=True` gives the sliding highlight animation — no keyframe tricks needed, the Avalonia core handles this natively.
- `CornerRadius="2"` makes it a pill-shaped strip instead of a rectangular bar.

When `IsChatRunning` transitions to false, the brain ProgressBar is hidden (`IsVisible=false`) and takes zero vertical space. When true, it appears as a 4px pill at the bottom of the chat output area.

The status line follows the muted text convention: `Theme.Class.muted.Foreground` (`#B3B3B3`) for labels and regular values, with key identifiers (model name) in accent color (`Theme.Class.accent.Foreground`, `#5EA0FF`). Segments are separated by a bullet character (`•`) in the muted color.

The entire row is compact: 12px font size, no padding above/below beyond what's natural for the container.

## Architecture

### Integration Point

The status line is added to the **conversation detail** template inside `AgentChatEditorControl.axaml`, not to `AgentChatOutputControl` directly. The conversation detail uses a `DockPanel` with the output control on top and the input queue at the bottom. The status line sits below both, docked at the bottom of the same panel:

```
┌─────────────────────────────┐
│   AgentChatOutputControl    │  (output / chat history)
│                             │
├─────────────────────────────┤
│   AgentChatInputQueueCtrl   │  (input editor + queue list)
├─────────────────────────────┤
│   [🧠 model] • [provider] • [credits] • [tokens]  │
└─────────────────────────────┘
```

### Data Flow

The status line binds to `AgentViewModel` properties. No new view models are created for this feature — the existing `AgentViewModel` already exposes all needed data except credits/tokens, which require upstream additions in `AgentChat`.

## Source Layout / Code Changes

### New Files to Create

#### 1. `Phantom.Workspaces.Agent.Gui\ViewModels\AgentChatStatusLineViewModel.cs`

A lightweight view model that aggregates the status line data from `AgentViewModel`:

```csharp
public sealed class AgentChatStatusLineViewModel : ViewModelBase
{
    private readonly AgentViewModel _agent;
    private bool _isThinking;
    private string _modelDisplay = "(none)";
    private string _providerDisplay = "(none)";
    private string? _creditsDisplay;
    private string? _tokensDisplay;

    public AgentChatStatusLineViewModel(AgentViewModel agent)
    {
        _agent = agent;
        SyncFromAgent();
        _agent.PropertyChanged += OnAgentPropertyChanged;
    }

    /// <summary>Whether the brain emoji should be visible.</summary>
    public bool IsThinking
    {
        get => _isThinking;
        private set => SetProperty(ref _isThinking, value);
    }

    public string ModelDisplay
    {
        get => _modelDisplay;
        private set => SetProperty(ref _modelDisplay, value);
    }

    public string ProviderDisplay
    {
        get => _providerDisplay;
        private set => SetProperty(ref _providerDisplay, value);
    }

    public string? CreditsDisplay
    {
        get => _creditsDisplay;
        private set => SetProperty(ref _creditsDisplay, value);
    }

    public string? TokensDisplay
    {
        get => _tokensDisplay;
        private set => SetProperty(ref _tokensDisplay, value);
    }

    public bool HasVisibleContent => !string.IsNullOrEmpty(ModelDisplay) || HasVisibleMetrics();

    private bool HasVisibleMetrics()
        => !string.IsNullOrEmpty(CreditsDisplay) || !string.IsNullOrEmpty(TokensDisplay);

    private void SyncFromAgent()
    {
        IsThinking = _agent.IsChatRunning;
        ModelDisplay = string.IsNullOrWhiteSpace(_agent.ModelId) ? "(none)" : _agent.ModelId;
        ProviderDisplay = string.IsNullOrWhiteSpace(_agent.ModelProvider) ? "(none)" : _agent.ModelProvider;
        CreditsDisplay = _agent.TotalCredits?.ToString("F2");
        TokensDisplay = _agent.TotalTokensIn.HasValue && _agent.TotalTokensOut.HasValue
            ? $"{_agent.TotalTokensIn:N0} in / {_agent.TotalTokensOut:N0} out"
            : null;
    }

    private void OnAgentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(AgentViewModel.IsChatRunning), StringComparison.Ordinal))
        {
            IsThinking = _agent.IsChatRunning;
        }
        else if (string.Equals(e.PropertyName, nameof(AgentViewModel.ModelId), StringComparison.Ordinal))
        {
            ModelDisplay = string.IsNullOrWhiteSpace(_agent.ModelId) ? "(none)" : _agent.ModelId;
        }
        else if (string.Equals(e.PropertyName, nameof(AgentViewModel.ModelProvider), StringComparison.Ordinal))
        {
            ProviderDisplay = string.IsNullOrWhiteSpace(_agent.ModelProvider) ? "(none)" : _agent.ModelProvider;
        }
        else if (string.Equals(e.PropertyName, nameof(AgentViewModel.TotalCredits), StringComparison.Ordinal))
        {
            CreditsDisplay = _agent.TotalCredits?.ToString("F2");
        }
        else if (string.Equals(e.PropertyName, nameof(AgentViewModel.TotalTokensIn), StringComparison.Ordinal)
              || string.Equals(e.PropertyName, nameof(AgentViewModel.TotalTokensOut), StringComparison.Ordinal))
        {
            TokensDisplay = (_agent.TotalTokensIn.HasValue && _agent.TotalTokensOut.HasValue)
                ? $"{_agent.TotalTokensIn:N0} in / {_agent.TotalTokensOut:N0} out"
                : null;
        }
    }

    public void Dispose() => _agent.PropertyChanged -= OnAgentPropertyChanged;
}
```

#### 2. `Phantom.Workspaces.Gui.Styles\Styles\AgentChatStatusLineStyles.axaml`

Centralized styles for the status line:

```xml
<AvaloniaResource xmlns="https://github.com/avaloniaui"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Styles.Resources>
        <!-- Pink brain emoji color -->
        <SolidColorBrush x:Key="AgentChat.ThinkingBrain.Foreground">#E86090</SolidColorBrush>

        <!-- Status line text styles -->
        <x:Double x:Key="AgentChat.StatusLine.FontSize">12</x:Double>
        <SolidColorBrush x:Key="AgentChat.StatusLine.Label.Foreground">#B3B3B3</SolidColorBrush>
        <SolidColorBrush x:Key="AgentChat.StatusLine.Value.Foreground">#5EA0FF</SolidColorBrush>
        <SolidColorBrush x:Key="AgentChat.StatusLine.Separator.Foreground">#4A4A4A</SolidColorBrush>

        <!-- Status line container -->
        <Thickness x:Key="AgentChat.StatusLine.Padding">12,8,12,8</Thickness>
    </Styles.Resources>

    <Style Selector="TextBlock.status-line-brain">
        <Setter Property="FontSize" Value="{DynamicResource AgentChat.StatusLine.FontSize}" />
        <Setter Property="Foreground" Value="{DynamicResource AgentChat.ThinkingBrain.Foreground}" />
        <Setter Property="FontWeight" Value="Normal" />
    </Style>

    <Style Selector="TextBlock.status-line-segment">
        <Setter Property="FontSize" Value="{DynamicResource AgentChat.StatusLine.FontSize}" />
        <Setter Property="Foreground" Value="{DynamicResource AgentChat.StatusLine.Value.Foreground}" />
        <Setter Property="FontWeight" Value="Normal" />
    </Style>

    <Style Selector="TextBlock.status-line-metric">
        <Setter Property="FontSize" Value="{DynamicResource AgentChat.StatusLine.FontSize}" />
        <Setter Property="Foreground" Value="{DynamicResource AgentChat.StatusLine.Label.Foreground}" />
        <Setter Property="FontWeight" Value="Normal" />
    </Style>

    <Style Selector="TextBlock.status-line-separator">
        <Setter Property="FontSize" Value="{DynamicResource AgentChat.StatusLine.FontSize}" />
        <Setter Property="Foreground" Value="{DynamicResource AgentChat.StatusLine.Separator.Foreground}" />
        <Setter Property="Padding" Value="8,0,8,0" />
    </Style>

    <!-- Brain pulsing animation style -->
    <Style Selector="TextBlock.status-line-brain">
        <Style.Triggers>
            <DataTrigger Binding="{Binding IsThinking}" Value="True">
                <Setter Property="Animation">
                    <Setter.Value>
                        <KeyFrameAnimation Duration="0:0:1.5">
                            <KeyFrame Cue="0%">
                                <Setter Property="Opacity" Value="0.4" />
                            </KeyFrame>
                            <KeyFrame Cue="50%">
                                <Setter Property="Opacity" Value="1" />
                            </KeyFrame>
                            <KeyFrame Cue="100%">
                                <Setter Property="Opacity" Value="0.4" />
                            </KeyFrame>
                        </KeyFrameAnimation>
                    </Setter.Value>
                </Setter>
            </DataTrigger>
        </Style.Triggers>
    </Style>
</AvaloniaResource>
```

### Files to Modify

#### 3. `Phantom.Workspaces.Agent.Gui\Controls\AgentChatEditorControl.axaml`

Add the status line to the conversation detail template. The existing `DockPanel` layout becomes:

**Before:**
```xml
<DockPanel>
    <controls:AgentChatInputQueueControl DockPanel.Dock="Bottom" ... />
    <controls:AgentChatOutputControl ... />
</DockPanel>
```

**After:**
```xml
<DockPanel>
    <controls:AgentChatInputQueueControl DockPanel.Dock="Bottom" ... />
    <Border DockPanel.Dock="Bottom" Padding="{DynamicResource AgentChat.StatusLine.Padding}">
        <TextBlock Text="{Binding StatusLine}" Classes="muted" FontSize="12" />
    </Border>
    <controls:AgentChatOutputControl ... />
</DockPanel>
```

The `StatusLine` text is a single formatted string built by the view model, using `\u00B7` (middle dot) as segment separators and inline formatting where supported. For Avalonia compatibility, we split the status line into multiple `TextBlock` elements with styles:

```xml
<DockPanel>
    <Border DockPanel.Dock="Bottom" Padding="{DynamicResource AgentChat.StatusLine.Padding}">
        <StackPanel Orientation="Horizontal">
            <!-- Thinking brain -->
            <TextBlock Classes="status-line-brain" IsVisible="{Binding StatusLine.IsThinking}" FontFamily="Segoe UI Emoji">🧠</TextBlock>
            
            <!-- Model name (if any) -->
            <TextBlock Classes="status-line-segment" Text="{Binding StatusLine.ModelDisplay}" IsVisible="{Binding StatusLine.HasModel}" />
            
            <!-- Separator before provider -->
            <TextBlock Classes="status-line-separator" Text="•" IsVisible="{Binding StatusLine.HasProvider}" />
            
            <!-- Provider name (if any) -->
            <TextBlock Classes="status-line-metric" Text="{Binding StatusLine.ProviderDisplay}" IsVisible="{Binding StatusLine.HasProvider}" />
            
            <!-- Separator before credits -->
            <TextBlock Classes="status-line-separator" Text="•" IsVisible="{Binding StatusLine.HasCredits}" />
            
            <!-- Credits (if tracked) -->
            <TextBlock Classes="status-line-metric" Text="{Binding StatusLine.CreditsDisplay}" IsVisible="{Binding StatusLine.HasCredits}" />
            
            <!-- Separator before tokens -->
            <TextBlock Classes="status-line-separator" Text="•" IsVisible="{Binding StatusLine.HasTokens}" />
            
            <!-- Token counts (if tracked) -->
            <TextBlock Classes="status-line-metric" Text="{Binding StatusLine.TokensDisplay}" IsVisible="{Binding StatusLine.HasTokens}" />
        </StackPanel>
    </Border>
    <controls:AgentChatOutputControl DataContext="{Binding Agent}" OutputMode="SelectableTextBox"/>
</DockPanel>
```

The binding context for the `DataTemplate` is the existing `AgentChatConversationDetailViewModel`. We add a `StatusLine` property to it that wraps an `AgentChatStatusLineViewModel`.

#### 4. `Phantom.Workspaces.Agent.Gui\ViewModels\AgentChatConversationDetailViewModel.cs`

Add a `StatusLine` property:

```csharp
public AgentChatStatusLineViewModel StatusLine { get; }

// In constructor:
this.StatusLine = new AgentChatStatusLineViewModel(agent);

// On dispose:
this.StatusLine?.Dispose();
```

#### 5. `Phantom.Workspaces.Llm.Core\AgentChat.cs` (upstream data)

Add credits and token tracking to `AgentChat`:

```csharp
/// <summary>Cumulative AI credits used in this session, if tracked by the provider.</summary>
public decimal? TotalCredits { get; private set; }

/// <summary>Cumulative input tokens consumed in this session, if tracked by the provider.</summary>
public long? TotalTokensIn { get; private set; }

/// <summary>Cumulative output tokens consumed in this session, if tracked by the provider.</summary>
public long? TotalTokensOut { get; private set; }
```

These are updated when the underlying `IChatClient` or chat client extension exposes usage telemetry. For GitHub Models / OpenAI providers this is available from `ChatResponseUpdate`; for Ollama it may not be available.

In `AgentViewModel`, wire through:

```csharp
public decimal? TotalCredits => agentChat.TotalCredits;
public long? TotalTokensIn => agentChat.TotalTokensIn;
public long? TotalTokensOut => agentChat.TotalTokensOut;
```

#### 6. `Phantom.Workspaces.Gui.Styles\Styles\SharedStyles.axaml` (color resources)

Add the pink brain color to the centralized theme resources if not in the dedicated status line styles file:

```xml
<SolidColorBrush x:Key="Theme.Status.ThinkingBrain">#E86090</SolidColorBrush>
```

### Tests to Write

#### `AgentChatStatusLineViewModelTests.cs` (new test file)

- **Empty agent**: When constructed with an agent whose model/provider are empty, `ModelDisplay` = "(none)", `ProviderDisplay` = "(none)", and `HasVisibleContent` reflects no visible segments.
- **Model change**: Changing `AgentViewModel.ModelId` updates `StatusLine.ModelDisplay` via property notification.
- **Provider change**: Same as model but for provider.
- **Thinking state**: When `AgentViewModel.IsChatRunning` transitions from false to true, `IsThinking` follows. When it transitions back to false, the brain hides.
- **Credits display**: When `TotalCredits` is non-null, `CreditsDisplay` shows the formatted value; when null, it remains null and `HasCredits` is false.
- **Token display**: When both `TotalTokensIn` and `TotalTokensOut` are non-null, `TokensDisplay` formats correctly; when either is null, tokens are hidden.
- **Disposal**: Disposing the status line removes the subscription from the agent.

#### `AgentChatEditorControlStatusLineTests.cs` (new test file)

- **DataTemplate rendering**: The conversation detail template exposes a `StatusLine` property with correct bindings.
- **Visibility rules**: When all metrics are hidden and model = "(none)", no segments render (container height collapses).
- **Thinking animation trigger**: When `IsThinking` is true, the brain TextBlock has `IsVisible = true`; when false, `IsVisible = false`.

#### `AgentChatUsageTrackingTests.cs` (new test file in `Phantom.Workspaces.Llm.Core.Tests`)

- **GitHub Models credits**: When running against a provider that returns usage data, `TotalCredits`, `TotalTokensIn`, and `TotalTokensOut` are updated after each turn.
- **Ollama no-usage fallback**: When the provider does not return usage data, the values remain null (no crash or false data).

## Resolved Decisions

1. **Single-line compact status.** The status line is always one row. No expand/collapse toggles; if more detail is needed, users open Chat Details.
2. **Brain as emoji, not icon resource.** We use the `🧠` Unicode character rather than an image asset to keep it lightweight and avoid resource management concerns. Font-family fallback ensures cross-platform rendering.
3. **No new colors in SharedStyles for status segments.** The pink brain goes in `SharedStyles.axaml` as a theme resource; segment text uses existing accent/muted brushes.
4. **Status line is view-model-driven, not inline-HTML.** Each segment is its own `TextBlock` with `IsVisible` binding — this is more reliable than trying to format rich text across Avalonia's limited `Inline` support and avoids issues like the FlowDocument rendering bugs already noted in the codebase.
5. **Credits/tokens are optional.** If a provider does not expose usage data, those segments simply do not render. No null-pointer or format errors.

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

/// <summary>
/// Maintains a per-item list of all diagnostic <see cref="AIContent"/> blocks collected from
/// the agent chat history, regardless of the <c>IsDiagnosticsVisible</c> toggle. Each item
/// exposes an inspect affordance that raises <see cref="InspectorRequested"/> so the view layer
/// can open <see cref="Controls.AIContentInspectorWindow"/> with the item's JSON payload.
/// </summary>
public sealed class DiagnosticInspectorViewModel : ViewModelBase, IDisposable
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    private readonly ReadOnlyObservableCollection<AgentChatHistoryItem> history;
    private readonly ObservableCollection<DiagnosticItemViewModel> items = [];
    private int nextId;

    public DiagnosticInspectorViewModel(ReadOnlyObservableCollection<AgentChatHistoryItem> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        this.history = history;
        this.Items = new ReadOnlyObservableCollection<DiagnosticItemViewModel>(this.items);

        foreach (var item in history)
        {
            this.AddDiagnosticItems(item);
        }

        if (history is INotifyCollectionChanged notifiable)
        {
            notifiable.CollectionChanged += this.OnHistoryChanged;
        }
    }

    /// <summary>
    /// Raised when the user activates the inspect affordance on a diagnostic item.
    /// The view layer subscribes to open <see cref="Controls.AIContentInspectorWindow"/>.
    /// </summary>
    public event EventHandler<DiagnosticInspectorRequestedEventArgs>? InspectorRequested;

    public ReadOnlyObservableCollection<DiagnosticItemViewModel> Items { get; }

    public void Dispose()
    {
        if (this.history is INotifyCollectionChanged notifiable)
        {
            notifiable.CollectionChanged -= this.OnHistoryChanged;
        }
    }

    private void OnHistoryChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is null)
        {
            return;
        }

        foreach (AgentChatHistoryItem item in e.NewItems)
        {
            this.AddDiagnosticItems(item);
        }
    }

    private void AddDiagnosticItems(AgentChatHistoryItem historyItem)
    {
        if (!string.Equals(
            historyItem.Role.Value,
            AgentChatHistoryItem.DiagnosticChatRole.Value,
            StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (var content in historyItem.Contents)
        {
            var contentId = $"diag-{this.nextId++}";
            var contentJson = SerializeContent(content);
            var item = new DiagnosticItemViewModel(
                contentId,
                contentJson,
                () => this.InspectorRequested?.Invoke(
                    this,
                    new DiagnosticInspectorRequestedEventArgs(contentId, contentJson)));
            this.items.Add(item);
        }
    }

    private static string SerializeContent(AIContent content)
    {
        try
        {
            return JsonSerializer.Serialize(content, PrettyJson);
        }
        catch
        {
            return content.ToString() ?? string.Empty;
        }
    }
}

public sealed class DiagnosticInspectorRequestedEventArgs : EventArgs
{
    public DiagnosticInspectorRequestedEventArgs(string contentId, string contentJson)
    {
        this.ContentId = contentId;
        this.ContentJson = contentJson;
    }

    public string ContentId { get; }

    public string ContentJson { get; }
}

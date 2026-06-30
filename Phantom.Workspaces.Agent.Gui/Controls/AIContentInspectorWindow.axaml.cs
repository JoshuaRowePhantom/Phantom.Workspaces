using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Agent.Gui.Controls;

/// <summary>
/// Read-only inspector window that shows the full metadata and raw payload of a single
/// <see cref="AIContent"/> item. Opened when the user clicks the inspect affordance on a chat
/// content block. Does not mutate chat history.
/// </summary>
public partial class AIContentInspectorWindow : Window
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    public AIContentInspectorWindow()
    {
        this.InitializeComponent();
    }

    public AIContentInspectorWindow(string contentId, string payloadJson)
    {
        this.InitializeComponent();
        this.Title = $"Inspect [{contentId}]";
        this.PayloadTextBox.Text = payloadJson;
    }

    public AIContentInspectorWindow(string contentId, AIContent content)
    {
        this.InitializeComponent();
        this.Title = $"Inspect: {content.GetType().Name} [{contentId}]";
        this.PayloadTextBox.Text = BuildPayload(content);
    }

    private static string BuildPayload(AIContent content)
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

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
        => this.Close();
}

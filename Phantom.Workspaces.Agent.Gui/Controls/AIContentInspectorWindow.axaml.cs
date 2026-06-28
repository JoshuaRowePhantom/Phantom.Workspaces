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

    public AIContentInspectorWindow(string contentId, AIContent content)
    {
        this.InitializeComponent();
        this.Title = $"Inspect: {content.GetType().Name} [{contentId}]";
        this.PayloadTextBox.Text = BuildPayload(contentId, content);
    }

    private static string BuildPayload(string contentId, AIContent content)
    {
        var info = new Dictionary<string, object?>
        {
            ["contentId"] = contentId,
            ["contentType"] = content.GetType().FullName,
        };

        switch (content)
        {
            case FunctionCallContent call:
                info["name"] = call.Name;
                info["callId"] = call.CallId;
                info["arguments"] = call.Arguments;
                break;
            case FunctionResultContent result:
                info["callId"] = result.CallId;
                info["result"] = result.Result;
                break;
            case TextContent text:
                info["text"] = text.Text;
                break;
            case TextReasoningContent reasoning:
                info["text"] = reasoning.Text;
                break;
            case ErrorContent error:
                info["message"] = error.Message;
                break;
            case UriContent uri:
                info["uri"] = uri.Uri.ToString();
                break;
            case DataContent data:
                info["mediaType"] = data.MediaType;
                info["dataLength"] = data.Data.Length;
                break;
            default:
                info["toString"] = content.ToString();
                break;
        }

        if (content.AdditionalProperties is { Count: > 0 } extra)
        {
            info["additionalProperties"] = extra;
        }

        try
        {
            return JsonSerializer.Serialize(info, PrettyJson);
        }
        catch
        {
            return info.ToString() ?? string.Empty;
        }
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
        => this.Close();
}

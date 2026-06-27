using System.Collections.Generic;
using System.Text.Json;

namespace Phantom.Workspaces.Agent.Gui.Controls;

/// <summary>
/// Builds the JSON command payloads the host posts into the browser-hosted chat-output page. Pure and
/// testable; the renderer control serializes operations with these helpers and delivers them through
/// the WebView bridge, where the page's <c>applyCommand</c> handler dispatches them.
/// </summary>
public static class ChatOutputBrowserCommands
{
    public const string UpdateType = "update";
    public const string RemoveType = "remove";
    public const string ScrollType = "scroll";
    public const string ThemeType = "theme";

    public static string Update(string path, string location, string content)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = UpdateType,
            ["path"] = path,
            ["location"] = location,
            ["content"] = content,
        });

    public static string Remove(string path)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = RemoveType,
            ["path"] = path,
        });

    public static string Scroll()
        => JsonSerializer.Serialize(new Dictionary<string, object?> { ["type"] = ScrollType });

    public static string Theme(IReadOnlyDictionary<string, string> variables)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = ThemeType,
            ["variables"] = variables,
        });
}

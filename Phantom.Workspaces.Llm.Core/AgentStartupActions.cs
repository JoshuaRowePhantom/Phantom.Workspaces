using System.Runtime.CompilerServices;

namespace Phantom.Workspaces.Llm;

public static class AgentStartupActions
{
    private static readonly ConditionalWeakTable<IAgentChat, Dictionary<string, Func<Task>>> actions = new();

    internal static string Register(IAgentChat chat, Func<Task> action)
    {
        var id = Guid.NewGuid().ToString("N");
        var entries = actions.GetOrCreateValue(chat);
        lock (entries)
            entries.Add(id, action);
        return "phantom-startup:" + id;
    }

    internal static void Remove(IAgentChat chat, string href)
    {
        if (actions.TryGetValue(chat, out var entries))
            lock (entries)
                entries.Remove(href["phantom-startup:".Length..]);
    }

    public static async Task<bool> TryInvokeAsync(IAgentChat chat, string href)
    {
        if (!href.StartsWith("phantom-startup:", StringComparison.Ordinal))
            return false;
        Func<Task>? action = null;
        if (actions.TryGetValue(chat, out var entries))
            lock (entries)
                entries.TryGetValue(href["phantom-startup:".Length..], out action);
        if (action is not null)
            await action().ConfigureAwait(false);
        return true;
    }
}

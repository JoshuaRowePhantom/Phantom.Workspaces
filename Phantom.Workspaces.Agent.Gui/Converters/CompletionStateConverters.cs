using System.Globalization;
using Avalonia.Data.Converters;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.Converters;

/// <summary>
/// Value converters for displaying <see cref="AgentChatCompletionState"/> in browser-card items.
/// </summary>
public static class CompletionStateConverters
{
    public static readonly IValueConverter IsRunning = new LambdaConverter(
        v => v is AgentChatCompletionState state && state == AgentChatCompletionState.Running);

    public static readonly IValueConverter IsSucceeded = new LambdaConverter(
        v => v is AgentChatCompletionState state && state == AgentChatCompletionState.Succeeded);

    public static readonly IValueConverter IsFailed = new LambdaConverter(
        v => v is AgentChatCompletionState state && state == AgentChatCompletionState.Failed);

    private sealed class LambdaConverter(Func<object?, bool> convert) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => convert(value);

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}

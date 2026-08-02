using AgentSchema;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Phantom.Workspaces.Agent.Gui.Controls;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.SlashCommands;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class QueueComposerControlTests
{
    private static AgentDefinition CreateAgentDefinition()
        => AgentDefinitionLoader.LoadAgentFromJson("""
        {
          "kind": "prompt",
          "name": "test-agent",
          "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
        }
        """);

    private static Window ShowInWindow(Control content)
    {
        var window = new Window
        {
            Width = 400,
            Height = 300,
            Content = content,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static TextBox GetInputBox(QueueComposerControl control)
        => (TextBox)control.FindControl<TextBox>("InputBox")!;

    [AvaloniaFact(Timeout = 15_000)]
    public async Task InputBox_DownArrow_WhenCompletionsVisible_AdvancesSelectionByExactlyOne()
    {
        // #1192: previously the KeyDown handler was registered for Tunnel|Bubble with
        // handledEventsToo:true, so a single physical Down press invoked the handler twice,
        // advancing the popup selection by 2 instead of 1.
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        var inputQueue = new InputQueueViewModel(chat, chat.DefaultInputQueue);
        var composer = inputQueue.DefaultComposer;
        composer.Completions.SetItems([
            new SlashCommandCompletion("alpha", "/alpha", "d"),
            new SlashCommandCompletion("beta", "/beta", "d"),
            new SlashCommandCompletion("gamma", "/gamma", "d"),
        ]);
        composer.Completions.SelectedIndex = 0;

        var control = new QueueComposerControl { DataContext = composer };
        _ = ShowInWindow(control);
        var inputBox = GetInputBox(control);
        inputBox.Focus();
        Dispatcher.UIThread.RunJobs();

        inputBox.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Down,
            KeyModifiers = KeyModifiers.None,
            Source = inputBox,
        });
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, composer.Completions.SelectedIndex);

        inputQueue.Dispose();
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task InputBox_UpArrow_WhenCompletionsVisible_MovesSelectionBackByExactlyOne()
    {
        // #1192 mirror of Down.
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        var inputQueue = new InputQueueViewModel(chat, chat.DefaultInputQueue);
        var composer = inputQueue.DefaultComposer;
        composer.Completions.SetItems([
            new SlashCommandCompletion("alpha", "/alpha", "d"),
            new SlashCommandCompletion("beta", "/beta", "d"),
            new SlashCommandCompletion("gamma", "/gamma", "d"),
        ]);
        composer.Completions.SelectedIndex = 2;

        var control = new QueueComposerControl { DataContext = composer };
        _ = ShowInWindow(control);
        var inputBox = GetInputBox(control);
        inputBox.Focus();
        Dispatcher.UIThread.RunJobs();

        inputBox.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Up,
            KeyModifiers = KeyModifiers.None,
            Source = inputBox,
        });
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, composer.Completions.SelectedIndex);

        inputQueue.Dispose();
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task InputBox_DownArrow_WhenCompletionsVisible_IsMarkedHandled()
    {
        // The KeyDown handler must still set e.Handled so ancestors do not react.
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        var inputQueue = new InputQueueViewModel(chat, chat.DefaultInputQueue);
        var composer = inputQueue.DefaultComposer;
        composer.Completions.SetItems([
            new SlashCommandCompletion("alpha", "/alpha", "d"),
            new SlashCommandCompletion("beta", "/beta", "d"),
        ]);
        composer.Completions.SelectedIndex = 0;

        var control = new QueueComposerControl { DataContext = composer };
        _ = ShowInWindow(control);
        var inputBox = GetInputBox(control);
        inputBox.Focus();
        Dispatcher.UIThread.RunJobs();

        var args = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Down,
            KeyModifiers = KeyModifiers.None,
            Source = inputBox,
        };
        inputBox.RaiseEvent(args);
        Dispatcher.UIThread.RunJobs();

        Assert.True(args.Handled);

        inputQueue.Dispose();
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task InputBox_EnterKey_WithText_SubmitsExactlyOnce()
    {
        // Regression: ensure the routing-strategy change did not break Enter submitting the
        // composer. Enter should submit exactly once (not twice) and post one queue item.
        await using var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = CreateAgentDefinition() });
        var inputQueue = new InputQueueViewModel(chat, chat.DefaultInputQueue, chat.InputQueueManager);
        var composer = inputQueue.DefaultComposer;
        composer.InputText = "hello";

        var control = new QueueComposerControl { DataContext = composer };
        _ = ShowInWindow(control);
        var inputBox = GetInputBox(control);
        inputBox.Text = "hello";
        inputBox.Focus();
        Dispatcher.UIThread.RunJobs();

        inputBox.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Enter,
            KeyModifiers = KeyModifiers.None,
            Source = inputBox,
        });
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(string.Empty, composer.InputText);

        inputQueue.Dispose();
    }
}

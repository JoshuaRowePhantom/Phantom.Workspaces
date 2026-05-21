using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Echo;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OllamaSharp;
using System.CommandLine;

var providerOption = new Option<string>("--provider", ["-p"])
{
    Description = "LLM provider: 'echo' (default), 'ollama-local', or 'ollama-remote'",
    DefaultValueFactory = _ => "echo",
};

var ollamaUrlOption = new Option<string?>("--ollama-url", ["-u"])
{
    Description = "URL for remote Ollama instance (e.g., http://192.168.1.100:11434)",
};

var ollamaModelOption = new Option<string?>("--model", ["-m"])
{
    Description = "Model name for Ollama (e.g., 'mistral', 'llama2')",
};

var thinkingOption = new Option<string>("--think", ["--thinking"])
{
    Description = "Thinking level: true/on/high (default), medium, low, false/off/none",
    DefaultValueFactory = _ => "true",
};

var rootCommand = new RootCommand("Phantom Workspaces LLM Agent CLI")
{
    providerOption,
    ollamaUrlOption,
    ollamaModelOption,
    thinkingOption,
};

rootCommand.SetAction(async (parseResult, ct) =>
{
    var provider = parseResult.GetValue(providerOption)!;
    var ollamaUrl = parseResult.GetValue(ollamaUrlOption);
    var ollamaModel = parseResult.GetValue(ollamaModelOption);
    var thinkingLevel = ParseThinkingLevel(parseResult.GetValue(thinkingOption));

    using var app = new AgentCliApp(provider, ollamaUrl, ollamaModel, thinkingLevel);
    await app.RunAsync();
});

await rootCommand.Parse(args).InvokeAsync();

static ReasoningEffort? ParseThinkingLevel(string? value) => value?.ToLowerInvariant() switch
{
    null or "true" or "on" or "high" => ReasoningEffort.High,
    "medium" or "med" => ReasoningEffort.Medium,
    "low" => ReasoningEffort.Low,
    "false" or "off" or "none" => ReasoningEffort.None,
    _ => throw new InvalidOperationException($"Unknown thinking level '{value}'. Use: true, false, low, medium, high"),
};

internal sealed class AgentCliApp : IDisposable
{
    private readonly object consoleLock = new();
    private readonly IChatClient chatClient;
    private readonly AgentInputQueueManager inputQueueManager;
    private readonly CancellationTokenSource appCts = new();
    private readonly bool supportsInteractiveRendering;
    private readonly string clientDisplayName;

    // Console layout state — all accessed under consoleLock
    private int assistantRow = -1;  // row of "assistant > ..." line; -1 when none active
    private int inputRow = -1;      // row of " > ..." prompt line
    private string currentInput = "";

    public AgentCliApp(string provider, string? ollamaUrl = null, string? ollamaModel = null, ReasoningEffort? thinkingLevel = ReasoningEffort.High)
    {
        (this.chatClient, this.clientDisplayName) = CreateChatClient(provider, ollamaUrl, ollamaModel);
        this.supportsInteractiveRendering = !Console.IsOutputRedirected && !Console.IsInputRedirected;
        var agent = new ChatClientAgent(
            this.chatClient,
            new ChatClientAgentOptions
            {
                ChatOptions = new ChatOptions
                {
                    Reasoning = new ReasoningOptions { Effort = thinkingLevel },
                },
            });
        this.inputQueueManager = new AgentInputQueueManager(agent);
    }

    private static (IChatClient, string displayName) CreateChatClient(string provider, string? ollamaUrl, string? ollamaModel)
    {
        return provider.ToLowerInvariant() switch
        {
            "echo" => (new EchoChatClient(), "Echo Chat Client"),
            "ollama-local" => CreateOllamaClient("http://localhost:11434", ollamaModel),
            "ollama-remote" when !string.IsNullOrWhiteSpace(ollamaUrl) => CreateOllamaClient(ollamaUrl, ollamaModel),
            "ollama-remote" => throw new InvalidOperationException(
                "ollama-remote provider requires --ollama-url option. Example: --provider ollama-remote --ollama-url http://192.168.1.100:11434"),
            _ => throw new InvalidOperationException($"Unknown provider: {provider}. Use 'echo', 'ollama-local', or 'ollama-remote'.")
        };
    }

    private static (IChatClient, string displayName) CreateOllamaClient(string baseUrl, string? model)
    {
        var modelName = model ?? "mistral";
        var client = new OllamaApiClient(new Uri(baseUrl), modelName);
        return (client, $"Ollama ({modelName} at {baseUrl})");
    }

    public async Task RunAsync()
    {
        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            e.Cancel = true;
            this.inputQueueManager.RequestInterrupt();
        };
        Console.CancelKeyPress += cancelHandler;

        Console.WriteLine($"{this.clientDisplayName} - Press Ctrl+C to interrupt. Type /exit to quit.");

        using var processCts = CancellationTokenSource.CreateLinkedTokenSource(this.appCts.Token);
        var processTask = Task.Run(() => this.ProcessUpdatesAsync(processCts.Token));

        try
        {
            while (!this.appCts.IsCancellationRequested)
            {
                var input = await this.ReadInputAsync(this.appCts.Token);
                if (input is null || string.Equals(input, "/exit", StringComparison.OrdinalIgnoreCase))
                    break;
                if (string.IsNullOrWhiteSpace(input))
                    continue;

                if (this.supportsInteractiveRendering)
                    this.SetupAssistantLayout(input);

                this.inputQueueManager.Enqueue(
                    this.inputQueueManager.ImmediateQueue,
                    [new ChatMessage(ChatRole.User, input)]);
            }
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            this.appCts.Cancel();
            this.inputQueueManager.Complete();
            processCts.Cancel();
            try { await processTask; } catch (OperationCanceledException) { }
        }
    }

    public void Dispose()
    {
        this.appCts.Dispose();
        this.chatClient?.Dispose();
    }

    private async Task<string?> ReadInputAsync(CancellationToken ct)
    {
        if (!this.supportsInteractiveRendering)
        {
            Console.Write(" > ");
            return Console.ReadLine();
        }

        lock (this.consoleLock)
        {
            this.inputRow = Console.CursorTop;
            Console.Write(" > ");
        }

        var buffer = new System.Text.StringBuilder();

        while (!ct.IsCancellationRequested)
        {
            if (!Console.KeyAvailable)
            {
                await Task.Delay(20, ct).ConfigureAwait(false);
                continue;
            }

            var key = Console.ReadKey(intercept: true);

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    return buffer.ToString();

                case ConsoleKey.Backspace when buffer.Length > 0:
                    buffer.Remove(buffer.Length - 1, 1);
                    lock (this.consoleLock)
                    {
                        this.currentInput = buffer.ToString();
                        this.RedrawInputLine();
                    }
                    break;

                default:
                    if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
                    {
                        buffer.Append(key.KeyChar);
                        lock (this.consoleLock)
                        {
                            this.currentInput = buffer.ToString();
                            this.RedrawInputLine();
                        }
                    }
                    break;
            }
        }

        return null;
    }

    // Sets up a blank assistant line and a new input prompt below the submitted line.
    // Must only be called from the input loop (not under consoleLock).
    private void SetupAssistantLayout(string submittedInput)
    {
        lock (this.consoleLock)
        {
            // Finalise the submitted input line in place
            var submittedLine = $" > {submittedInput}";
            Console.SetCursorPosition(0, this.inputRow);
            Console.Write(submittedLine + new string(' ', Math.Max(0, Console.BufferWidth - submittedLine.Length - 1)));

            // Emit two new lines: one blank (agent), one for next input.
            // Console.CursorTop after these tells us the real rows after any scroll.
            Console.WriteLine();
            Console.WriteLine();

            this.inputRow = Console.CursorTop;
            this.assistantRow = this.inputRow - 1;
            this.currentInput = "";

            Console.Write(" > ");
        }
    }

    // Must be called under consoleLock.
    private void RedrawInputLine()
    {
        if (this.inputRow < 0)
            return;
        var line = $" > {this.currentInput}";
        Console.SetCursorPosition(0, this.inputRow);
        Console.Write(line + new string(' ', Math.Max(0, Console.BufferWidth - line.Length - 1)));
        Console.SetCursorPosition(Math.Min(line.Length, Console.BufferWidth - 1), this.inputRow);
    }

    private async Task ProcessUpdatesAsync(CancellationToken ct)
    {
        var accumulated = new System.Text.StringBuilder();

        await foreach (var update in this.inputQueueManager.Process(ct).WithCancellation(ct))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                accumulated.Append(update.Text);
                this.RenderAssistantChunk(accumulated.ToString());
            }

            if (update.FinishReason is not null)
            {
                this.FinalizeAssistantTurn();
                accumulated.Clear();
            }
        }
    }

    private void RenderAssistantChunk(string accumulatedText)
    {
        if (!this.supportsInteractiveRendering)
        {
            // Non-interactive: overwrite same line with \r until turn ends
            lock (this.consoleLock)
            {
                Console.Write($"\rassistant > {accumulatedText}");
            }
            return;
        }

        lock (this.consoleLock)
        {
            if (this.assistantRow < 0)
                return;

            var line = $"assistant > {accumulatedText}";
            if (line.Length > Console.BufferWidth - 1)
                line = line[..(Console.BufferWidth - 1)];

            Console.SetCursorPosition(0, this.assistantRow);
            Console.Write(line + new string(' ', Math.Max(0, Console.BufferWidth - line.Length - 1)));

            // Restore cursor to end of the user's current input
            this.RedrawInputLine();
        }
    }

    private void FinalizeAssistantTurn()
    {
        if (!this.supportsInteractiveRendering)
        {
            lock (this.consoleLock)
            {
                Console.WriteLine();
            }
            return;
        }

        lock (this.consoleLock)
        {
            this.assistantRow = -1;
        }
    }
}

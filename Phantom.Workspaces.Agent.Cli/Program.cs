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
    private readonly CancellationTokenSource cancellationTokenSource = new();
    private readonly CancellationTokenSource processCancellationTokenSource = new();
    private readonly bool supportsInPlaceRendering = !Console.IsOutputRedirected;
    private int? assistantLineTop;
    private readonly string clientDisplayName;

    public AgentCliApp(string provider, string? ollamaUrl = null, string? ollamaModel = null, ReasoningEffort? thinkingLevel = ReasoningEffort.High)
    {
        (this.chatClient, this.clientDisplayName) = CreateChatClient(provider, ollamaUrl, ollamaModel);
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
        var uri = new Uri(baseUrl);
        var client = new OllamaApiClient(uri, modelName);
        return (client, $"Ollama ({modelName} at {baseUrl})");
    }

    public async Task RunAsync()
    {
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            this.inputQueueManager.RequestInterrupt();
        };
        Console.CancelKeyPress += cancelHandler;

        WriteLine($"{this.clientDisplayName} - Press Ctrl+C to interrupt. Type /exit to quit.");

        var processTask = Task.Run(
            () => this.ProcessUpdatesAsync(this.processCancellationTokenSource.Token),
            this.cancellationTokenSource.Token);

        try
        {
            while (!this.cancellationTokenSource.IsCancellationRequested)
            {
                lock (this.consoleLock)
                {
                    Console.Write(" > ");
                }

                var line = Console.ReadLine();
                if (line is null
                    || string.Equals(line, "/exit", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                this.inputQueueManager.Enqueue(
                    this.inputQueueManager.ImmediateQueue,
                    [new ChatMessage(ChatRole.User, line)]);
            }
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            this.cancellationTokenSource.Cancel();
            this.inputQueueManager.Complete();
            this.processCancellationTokenSource.Cancel();
            try
            {
                await processTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public void Dispose()
    {
        this.cancellationTokenSource.Dispose();
        this.processCancellationTokenSource.Dispose();
        this.chatClient?.Dispose();
    }

    private async Task ProcessUpdatesAsync(
        CancellationToken cancellationToken)
    {
        var accumulated = new System.Text.StringBuilder();

        await foreach (var update in this.inputQueueManager.Process(cancellationToken).WithCancellation(cancellationToken))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                accumulated.Append(update.Text);
                this.RenderAssistantUpdate(accumulated.ToString());
            }

            if (update.FinishReason is not null)
            {
                this.FinishAssistantTurn();
                accumulated.Clear();
            }
        }
    }

    private void FinishAssistantTurn()
    {
        lock (this.consoleLock)
        {
            if (this.assistantLineTop is not null)
            {
                // Move cursor to end of the assistant line so the next output starts below it
                Console.SetCursorPosition(0, this.assistantLineTop.Value + 1);
                this.assistantLineTop = null;
            }
        }
    }

    private void RenderAssistantUpdate(
        string text)
    {
        if (!this.supportsInPlaceRendering)
        {
            this.WriteLine($"assistant > {text}");
            return;
        }

        lock (this.consoleLock)
        {
            if (this.assistantLineTop is null)
            {
                Console.WriteLine($"assistant > {text}");
                this.assistantLineTop = Console.CursorTop - 1;
                return;
            }
        }

        this.ReplaceAssistantLine(text);
    }

    private void ReplaceAssistantLine(
        string text)
    {
        if (!this.supportsInPlaceRendering)
        {
            this.WriteLine($"assistant > {text}");
            return;
        }

        lock (this.consoleLock)
        {
            if (this.assistantLineTop is null)
            {
                Console.WriteLine($"assistant > {text}");
                this.assistantLineTop = Console.CursorTop - 1;
                return;
            }

            var restoreLeft = Console.CursorLeft;
            var restoreTop = Console.CursorTop;
            var lineTop = Math.Clamp(this.assistantLineTop.Value, 0, Console.BufferHeight - 1);
            Console.SetCursorPosition(0, lineTop);
            Console.Write(new string(' ', Math.Max(1, Console.BufferWidth - 1)));
            Console.SetCursorPosition(0, lineTop);
            Console.Write($"assistant > {text}");
            Console.SetCursorPosition(restoreLeft, restoreTop);
        }
    }

    private void WriteLine(
        string line)
    {
        lock (this.consoleLock)
        {
            Console.WriteLine(line);
        }
    }
}


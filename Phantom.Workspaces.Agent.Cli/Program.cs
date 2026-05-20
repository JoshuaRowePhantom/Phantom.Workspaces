using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Echo;
using Phantom.Workspaces.Llm.Provider.Llama;

var options = CliOptions.Parse(args);
using var app = new AgentCliApp(options);
await app.RunAsync();

internal sealed class AgentCliApp : IDisposable
{
    private readonly CliOptions options;
    private readonly object consoleLock = new();
    private HttpClient? httpClient;
    private readonly AgentInputQueueManager inputQueueManager;
    private readonly AgentSession session;
    private readonly CancellationTokenSource cancellationTokenSource = new();
    private readonly CancellationTokenSource processCancellationTokenSource = new();
    private readonly bool supportsInPlaceRendering = !Console.IsOutputRedirected;
    private int? assistantLineTop;

    public AgentCliApp(
        CliOptions options)
    {
        this.options = options;
        var provider = this.CreateProvider();
        this.session = AgentSession.Create(
            LlmSessionBuilder.Create().Build(),
            AgentExecutionEnvironmentDispatcher.Empty,
            new ProjectorLlmProvider(provider));
        this.inputQueueManager = new AgentInputQueueManager(this.session);
    }

    public async Task RunAsync()
    {
        WriteLine($"Provider: {this.options.Provider}");
        if (this.options.Provider == ProviderKind.Ollama)
        {
            WriteLine($"Model: {this.options.Model}");
            WriteLine($"Endpoint: {this.options.Endpoint}");
            if (!string.IsNullOrWhiteSpace(this.options.Think))
            {
                WriteLine($"Think: {this.options.Think}");
            }
        }

        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            this.inputQueueManager.RequestInterrupt();
        };
        Console.CancelKeyPress += cancelHandler;

        WriteLine("Press Ctrl+C to interrupt the current response. Type /exit to quit.");
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
                    [new LlmEvent
                    {
                        EventKind = LlmEventKinds.Turn,
                        Role = LlmRoles.User,
                        Content = line,
                    }]);
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
        this.httpClient?.Dispose();
    }

    private async Task ProcessUpdatesAsync(
        CancellationToken cancellationToken)
    {
        await foreach (var update in this.inputQueueManager.Process(cancellationToken).WithCancellation(cancellationToken))
        {
            var streamEvent = update.LlmStreamingEvent;
            if (streamEvent is null)
            {
                continue;
            }

            if (streamEvent.Replace?.Events is { Count: > 0 } replacementEvents)
            {
                var replacement = replacementEvents[^1];
                if (string.Equals(replacement.Role, LlmRoles.Assistant, StringComparison.Ordinal))
                {
                    this.ReplaceAssistantLine(FormatAssistant(replacement));
                    if (replacement.Done == true)
                    {
                        this.assistantLineTop = null;
                    }
                }

                continue;
            }

            if (streamEvent.Event is null)
            {
                continue;
            }

            var llmEvent = streamEvent.Event;
            if (string.Equals(llmEvent.Role, LlmRoles.Assistant, StringComparison.Ordinal))
            {
                this.RenderAssistantEvent(llmEvent);
                if (llmEvent.Done == true)
                {
                    this.assistantLineTop = null;
                }

                continue;
            }

            this.WriteLine(FormatEvent(llmEvent));
            this.assistantLineTop = null;
        }
    }

    private void RenderAssistantEvent(
        LlmEvent llmEvent)
    {
        var text = FormatAssistant(llmEvent);
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

    private static string FormatAssistant(
        LlmEvent llmEvent)
    {
        var content = llmEvent.Content ?? string.Empty;
        var thinking = llmEvent.Thinking ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(content) && !string.IsNullOrWhiteSpace(thinking))
        {
            return $"{content} [thinking: {thinking}]";
        }

        return !string.IsNullOrWhiteSpace(content)
            ? content
            : !string.IsNullOrWhiteSpace(thinking)
                ? $"[thinking: {thinking}]"
                : "(empty)";
    }

    private static string FormatEvent(
        LlmEvent llmEvent)
    {
        var role = llmEvent.Role ?? "event";
        var content = llmEvent.Content ?? llmEvent.Thinking ?? string.Empty;
        return $"{role} > {content}";
    }

    private ILlmProvider CreateProvider()
    {
        return this.options.Provider switch
        {
            ProviderKind.Echo => new EchoLlmProvider(),
            ProviderKind.Ollama => this.CreateOllamaProvider(),
            _ => throw new ArgumentOutOfRangeException(),
        };
    }

    private ILlmProvider CreateOllamaProvider()
    {
        this.httpClient = new HttpClient();
        return new OllamaHttpLlmProvider(
            this.httpClient,
            new OllamaOptions
            {
                Model = this.options.Model!,
                Endpoint = new Uri(this.options.Endpoint!),
                ThinkingLevel = this.options.Think,
            });
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

internal enum ProviderKind
{
    Ollama,
    Echo,
}

internal sealed record CliOptions
{
    public required ProviderKind Provider { get; init; }

    public string? Model { get; init; }

    public string? Think { get; init; }

    public string? Endpoint { get; init; }

    public static CliOptions Parse(
        string[] args)
    {
        string? provider = null;
        string? model = null;
        string? think = null;
        string? endpoint = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--provider", StringComparison.OrdinalIgnoreCase))
            {
                provider = ReadValue(args, ref i, "--provider");
                continue;
            }

            if (string.Equals(arg, "--model", StringComparison.OrdinalIgnoreCase))
            {
                model = ReadValue(args, ref i, "--model");
                continue;
            }

            if (string.Equals(arg, "--think", StringComparison.OrdinalIgnoreCase))
            {
                think = ReadValue(args, ref i, "--think");
                continue;
            }

            if (string.Equals(arg, "--endpoint", StringComparison.OrdinalIgnoreCase))
            {
                endpoint = ReadValue(args, ref i, "--endpoint");
                continue;
            }
        }

        if (!TryParseProvider(provider, out var providerKind))
        {
            throw new InvalidOperationException("Expected --provider with value 'ollama' or 'echo'.");
        }

        if (providerKind == ProviderKind.Ollama)
        {
            model ??= "qwen3.6";
            endpoint ??= OllamaOptions.LocalEndpoint;
        }

        return new CliOptions
        {
            Provider = providerKind,
            Model = model,
            Think = think,
            Endpoint = endpoint,
        };
    }

    private static string ReadValue(
        string[] args,
        ref int index,
        string optionName)
    {
        if (index + 1 >= args.Length)
        {
            throw new InvalidOperationException($"Missing value for {optionName}.");
        }

        index++;
        return args[index];
    }

    private static bool TryParseProvider(
        string? provider,
        out ProviderKind providerKind)
    {
        if (string.Equals(provider, "ollama", StringComparison.OrdinalIgnoreCase))
        {
            providerKind = ProviderKind.Ollama;
            return true;
        }

        if (string.Equals(provider, "echo", StringComparison.OrdinalIgnoreCase))
        {
            providerKind = ProviderKind.Echo;
            return true;
        }

        providerKind = default;
        return false;
    }
}

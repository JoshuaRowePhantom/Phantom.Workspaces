using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Echo;
using Microsoft.Extensions.AI;

var app = new AgentCliApp();
await app.RunAsync();

internal sealed class AgentCliApp : IDisposable
{
    private readonly object consoleLock = new();
    private readonly IChatClient chatClient;
    private readonly AgentInputQueueManager inputQueueManager;
    private readonly AgentSession session;
    private readonly CancellationTokenSource cancellationTokenSource = new();
    private readonly CancellationTokenSource processCancellationTokenSource = new();
    private readonly bool supportsInPlaceRendering = !Console.IsOutputRedirected;
    private int? assistantLineTop;

    public AgentCliApp()
    {
        this.chatClient = new EchoChatClient();
        this.session = AgentSession.Create(this.chatClient);
        this.inputQueueManager = new AgentInputQueueManager(this.session);
    }

    public async Task RunAsync()
    {
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            this.inputQueueManager.RequestInterrupt();
        };
        Console.CancelKeyPress += cancelHandler;

        WriteLine("Echo Chat Client - Press Ctrl+C to interrupt. Type /exit to quit.");
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
        await foreach (var update in this.inputQueueManager.Process(cancellationToken).WithCancellation(cancellationToken))
        {
            var responseUpdate = update.ResponseUpdate;
            if (responseUpdate is null || string.IsNullOrWhiteSpace(responseUpdate.Text))
            {
                continue;
            }

            this.RenderAssistantUpdate(responseUpdate.Text);
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

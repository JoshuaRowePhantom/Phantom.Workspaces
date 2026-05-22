using Phantom.Workspaces.Llm;
using AgentSchema;
using Microsoft.Extensions.AI;
using System.CommandLine;
using Phantom.Workspaces.Agent.Cli;

var definitionParser = new AgentDefinitionCommandLineParser();

var rootCommand = new RootCommand("Phantom Workspaces LLM Agent CLI")
{};
definitionParser.AddOptions(rootCommand);

rootCommand.SetAction(async (parseResult, ct) =>
{
    var cliParseResult = definitionParser.Parse(parseResult);
    using var app = new AgentCliApp(cliParseResult);
    await app.RunAsync();
});

await rootCommand.Parse(args).InvokeAsync();

internal sealed class AgentCliApp : IDisposable
{
    private readonly object consoleLock = new();
    private readonly IChatClient chatClient;
    private readonly AgentInputQueueManager inputQueueManager;
    private readonly CancellationTokenSource appCts = new();
    private readonly bool supportsInteractiveRendering;
    private readonly string clientDisplayName;

    // Console layout state — all accessed under consoleLock
    // Layout (when assistant is active):
    //   assistantBlockRow + 0 : blank
    //   assistantBlockRow + 1 : "  assistant [spinner]:" header (blue)
    //   assistantBlockRow + 2 : blank
    //   assistantBlockRow + 3..3+N-1 : N content rows (white)
    //   inputRow - 3           : blank separator
    //   inputRow - 2           : "  user:" header (green)
    //   inputRow - 1           : blank
    //   inputRow               : current input text (white)
    private int assistantBlockRow = -1;     // row of the blank before the assistant header; -1 when inactive
    private int assistantContentLines = 0;  // how many terminal rows the response content occupies
    private int inputRow = -1;              // row of the current input text
    private string currentInput = "";

    // Streaming / spinner state — all accessed under consoleLock
    private static readonly string[] SpinnerFrames = [".  ", ".. ", "..."];
    private int spinnerFrame = 0;
    private bool isStreaming = false;
    private string lastAccumulatedText = "";

    private string AssistantHeaderLine => this.isStreaming
        ? $"  assistant {SpinnerFrames[this.spinnerFrame]}:"
        : "  assistant:";

    public AgentCliApp(AgentDefinitionParseResult parseResult)
    {
        this.supportsInteractiveRendering = !Console.IsOutputRedirected && !Console.IsInputRedirected;

        var created = AgentFactory.CreateAgent(parseResult.AgentDefinition);

        this.chatClient = created.Client;
        this.clientDisplayName = created.DisplayName;

        if (!string.IsNullOrEmpty(parseResult.AgentSchemaPath))
        {
            this.clientDisplayName = $"{this.clientDisplayName} [from {Path.GetFileName(parseResult.AgentSchemaPath)}]";
        }

        this.inputQueueManager = new AgentInputQueueManager(created.Agent);
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
        var spinnerTask = Task.Run(() => this.SpinnerAsync(processCts.Token));

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
            try { await spinnerTask; } catch (OperationCanceledException) { }
        }
    }

    public void Dispose()
    {
        this.appCts.Dispose();
        this.chatClient?.Dispose();
    }

    private async Task SpinnerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(200, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            if (!this.supportsInteractiveRendering) continue;

            lock (this.consoleLock)
            {
                if (!this.isStreaming || this.assistantBlockRow < 0) continue;
                this.spinnerFrame = (this.spinnerFrame + 1) % SpinnerFrames.Length;
                this.RenderAssistantContentLocked();
            }
        }
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
            if (this.inputRow < 0)
            {
                // First call: emit three rows (blank, user-header placeholder, blank) so
                // RedrawInputLine can safely write to inputRow-3 / inputRow-2 / inputRow-1.
                Console.WriteLine();
                Console.WriteLine();
                Console.WriteLine();
                this.inputRow = Console.CursorTop;
            }
            this.currentInput = "";
            this.RedrawInputLine();
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

    // Sets up the assistant block and a new input prompt below the submitted text.
    // Must only be called from the input loop (not under consoleLock).
    private void SetupAssistantLayout(string submittedInput)
    {
        lock (this.consoleLock)
        {
            // Overwrite the current input line with the submitted text in white.
            var lineWidth = Console.BufferWidth - 1;
            Console.SetCursorPosition(0, this.inputRow);
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write(submittedInput + new string(' ', Math.Max(0, lineWidth - submittedInput.Length)));
            Console.ResetColor();

            // Emit 8 rows to lay out:
            //   [0] end of submitted-text row      → old inputRow (submitted text)
            //   [1] blank before assistant header  → assistantBlockRow + 0
            //   [2] assistant header placeholder   → assistantBlockRow + 1
            //   [3] blank after assistant header   → assistantBlockRow + 2
            //   [4] first content row placeholder  → assistantBlockRow + 3
            //   [5] blank separator after content  → inputRow - 3
            //   [6] user header placeholder        → inputRow - 2
            //   [7] blank after user header        → inputRow - 1
            // After all 8, CursorTop becomes the new inputRow.
            for (var i = 0; i < 8; i++)
                Console.WriteLine();

            this.inputRow = Console.CursorTop;
            this.assistantBlockRow = this.inputRow - 7;
            this.assistantContentLines = 1;
            this.isStreaming = true;
            this.spinnerFrame = 0;
            this.lastAccumulatedText = "";
            this.currentInput = "";

            // Immediately draw the assistant header and user header so the regions aren't blank.
            this.RenderAssistantContentLocked();
        }
    }

    // Must be called under consoleLock.
    private void RedrawInputLine()
    {
        if (this.inputRow < 3)
            return;

        var lineWidth = Console.BufferWidth - 1;

        // Blank separator before user header (also clears any stale content when region expands).
        Console.SetCursorPosition(0, this.inputRow - 3);
        Console.Write(new string(' ', lineWidth));

        // "  user:" header in green.
        Console.SetCursorPosition(0, this.inputRow - 2);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("  user:" + new string(' ', Math.Max(0, lineWidth - 7)));
        Console.ResetColor();

        // Blank after user header.
        Console.SetCursorPosition(0, this.inputRow - 1);
        Console.Write(new string(' ', lineWidth));

        // Current input text in white.
        Console.SetCursorPosition(0, this.inputRow);
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write(this.currentInput + new string(' ', Math.Max(0, lineWidth - this.currentInput.Length)));
        Console.ResetColor();

        Console.SetCursorPosition(Math.Min(this.currentInput.Length, Console.BufferWidth - 1), this.inputRow);
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
            lock (this.consoleLock)
                Console.Write($"\rassistant > {accumulatedText}");
            return;
        }

        lock (this.consoleLock)
        {
            this.lastAccumulatedText = accumulatedText;
            this.RenderAssistantContentLocked();
        }
    }

    // Renders the current accumulated assistant text with the current spinner frame.
    // Must be called under consoleLock.
    private void RenderAssistantContentLocked()
    {
        if (this.assistantBlockRow < 0)
            return;

        // Leave the last column empty to prevent forced terminal wrapping.
        var lineWidth = Console.BufferWidth - 1;

        // Split the raw content into display rows — no prefix; header is a separate row.
        var displayRows = GetDisplayRows(this.lastAccumulatedText, lineWidth);
        var requiredLines = Math.Max(1, displayRows.Count);

        // Expand the assistant region downward if the response has grown.
        // Stop if we've hit the top of the buffer (assistantBlockRow can't go negative).
        while (requiredLines > this.assistantContentLines)
        {
            var prevInputRow = this.inputRow;
            Console.SetCursorPosition(0, this.inputRow);
            Console.WriteLine();
            var afterRow = Console.CursorTop;

            var scrolled = (prevInputRow + 1) - afterRow;
            var newAssistantBlockRow = this.assistantBlockRow - scrolled;
            if (newAssistantBlockRow < 0)
            {
                // Buffer top reached — stop expanding; we'll show a sliding window below.
                this.inputRow = afterRow;
                break;
            }

            this.assistantBlockRow = newAssistantBlockRow;
            this.inputRow = afterRow;
            this.assistantContentLines++;
        }

        // Write the "  assistant [spinner]:" header in blue.
        Console.SetCursorPosition(0, this.assistantBlockRow + 1);
        Console.ForegroundColor = ConsoleColor.Blue;
        var header = this.AssistantHeaderLine;
        Console.Write(header + new string(' ', Math.Max(0, lineWidth - header.Length)));
        Console.ResetColor();

        // Clear the content region.
        for (var i = 0; i < this.assistantContentLines; i++)
        {
            Console.SetCursorPosition(0, this.assistantBlockRow + 3 + i);
            Console.Write(new string(' ', lineWidth));
        }

        // Write content rows in white, showing the last assistantContentLines rows when the
        // content overflows the allocated region (sliding window).
        var skipRows = Math.Max(0, displayRows.Count - this.assistantContentLines);
        Console.ForegroundColor = ConsoleColor.White;
        for (var i = 0; i < Math.Min(this.assistantContentLines, displayRows.Count); i++)
        {
            Console.SetCursorPosition(0, this.assistantBlockRow + 3 + i);
            Console.Write(displayRows[skipRows + i]);
        }
        Console.ResetColor();

        this.RedrawInputLine();
    }

    // Splits text into terminal-width display rows, honouring embedded newlines.
    // No returned string is longer than lineWidth, so Console.Write never causes
    // implicit cursor advancement to the next row.
    private static List<string> GetDisplayRows(string text, int lineWidth)
    {
        var rows = new List<string>();
        var logicalLines = text.Split('\n');

        // Drop a single trailing empty segment produced by a trailing newline.
        var lineCount = logicalLines.Length;
        if (lineCount > 1 && logicalLines[lineCount - 1].Length == 0)
            lineCount--;

        for (var li = 0; li < lineCount; li++)
        {
            var line = logicalLines[li];
            if (line.Length == 0)
            {
                rows.Add(string.Empty);
                continue;
            }
            for (var offset = 0; offset < line.Length; offset += lineWidth)
                rows.Add(line[offset..Math.Min(offset + lineWidth, line.Length)]);
        }

        return rows;
    }

    private void FinalizeAssistantTurn()
    {
        if (!this.supportsInteractiveRendering)
        {
            lock (this.consoleLock)
                Console.WriteLine();
            return;
        }

        lock (this.consoleLock)
        {
            this.isStreaming = false;
            // One final render to replace the spinner header with the plain "  assistant:" header.
            this.RenderAssistantContentLocked();
            this.assistantBlockRow = -1;
            this.assistantContentLines = 0;
        }
    }
}

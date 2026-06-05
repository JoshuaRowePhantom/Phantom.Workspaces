using Phantom.Workspaces.Llm;
using AgentSchema;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.CommandLine;

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
    private readonly AgentChat agentChat;
    private readonly CancellationTokenSource appCts = new();
    private readonly bool supportsInteractiveRendering;
    private readonly string clientDisplayName;
    private readonly ILoggerFactory? loggerFactory;

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
    private readonly System.Text.StringBuilder assistantAccumulatedText = new();
    private readonly List<string> currentLogLines = [];
    private int submittedTurnCount;
    private int completedTurnCount;

    private string AssistantHeaderLine => this.isStreaming
        ? $"  assistant {SpinnerFrames[this.spinnerFrame]}:"
        : "  assistant:";

    public AgentCliApp(AgentDefinitionParseResult parseResult)
    {
        this.supportsInteractiveRendering = !Console.IsOutputRedirected && !Console.IsInputRedirected;

        this.loggerFactory = (parseResult.LogChat || parseResult.LogHttpRequests)
            ? CreateConsoleLoggerFactory(this.WriteLogLine)
            : null;
        var services = new AgentServices
        {
            LogChat = parseResult.LogChat,
            LogHttpRequests = parseResult.LogHttpRequests,
            LoggerFactory = this.loggerFactory,
        };

        this.agentChat = AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentSessionId = parseResult.AgentSessionId,
                AgentDefinition = parseResult.AgentDefinition,
                AgentServices = services,
            }).GetAwaiter().GetResult();
        this.clientDisplayName = this.agentChat.DisplayName;

        if (!string.IsNullOrEmpty(parseResult.AgentSchemaPath))
        {
            this.clientDisplayName = $"{this.clientDisplayName} [from {Path.GetFileName(parseResult.AgentSchemaPath)}]";
        }

    }

    public async Task RunAsync()
    {
        this.agentChat.TurnCompleted += this.OnTurnCompleted;

        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            e.Cancel = true;
            this.ResetAssistantStreamAfterInterrupt();
            this.agentChat.Interrupt();
        };
        Console.CancelKeyPress += cancelHandler;

        Console.WriteLine($"{this.clientDisplayName} - Press Ctrl+C to interrupt. Type /exit to quit.");

        var spinnerTask = Task.Run(() => this.SpinnerAsync(this.appCts.Token));

        try
        {
            while (!this.appCts.IsCancellationRequested)
            {
                var input = await this.ReadInputAsync(this.appCts.Token);
                if (input is null || string.Equals(input, "/exit", StringComparison.OrdinalIgnoreCase))
                {
                    await this.WaitForConversationToSettleAsync(this.appCts.Token);
                    break;
                }
                if (string.IsNullOrWhiteSpace(input))
                    continue;

                this.ClearAssistantAccumulatedText();

                if (this.supportsInteractiveRendering)
                    this.SetupAssistantLayout(input);

                Interlocked.Increment(ref this.submittedTurnCount);
                this.agentChat.EnqueueUserMessage(input);
            }
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            this.agentChat.TurnCompleted -= this.OnTurnCompleted;
            this.appCts.Cancel();
            await this.agentChat.DisposeAsync();
            try { await spinnerTask; } catch (OperationCanceledException) { }
        }
    }

    public void Dispose()
    {
        this.appCts.Dispose();
        this.loggerFactory?.Dispose();
    }

    private ILoggerFactory CreateConsoleLoggerFactory(Action<string> onLogLine)
    {
        return LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddFilter("Microsoft.Extensions.AI", LogLevel.Trace);
            builder.AddFilter("Phantom.Workspaces.Llm", LogLevel.Trace);
            builder.ClearProviders();
            builder.AddProvider(new InteractiveConsoleLoggerProvider(onLogLine));
        });
    }

    private void WriteLogLine(string logLine)
    {
        var normalizedLogLine = logLine.Replace("\r", string.Empty);
        if (!this.supportsInteractiveRendering)
        {
            Console.WriteLine(normalizedLogLine);
            return;
        }

        lock (this.consoleLock)
        {
            if (this.assistantBlockRow < 0)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine(normalizedLogLine);
                Console.ResetColor();
                return;
            }

            this.currentLogLines.Add(normalizedLogLine);
            this.RenderAssistantContentLocked();
        }
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

    private async Task WaitForConversationToSettleAsync(CancellationToken ct)
    {
        var unresolvedTurnGracePeriod = TimeSpan.FromSeconds(5);
        var unresolvedTurnDeadline = DateTimeOffset.UtcNow + unresolvedTurnGracePeriod;

        while (!ct.IsCancellationRequested)
        {
            var hasQueuedMessages = this.agentChat.InputQueueManager.InputQueue.Any(queue => queue.Items.Count > 0);
            var submittedTurns = Volatile.Read(ref this.submittedTurnCount);
            var completedTurns = Volatile.Read(ref this.completedTurnCount);
            if (!hasQueuedMessages)
            {
                if (completedTurns >= submittedTurns || submittedTurns == 0 || DateTimeOffset.UtcNow >= unresolvedTurnDeadline)
                {
                    return;
                }
            }

            await Task.Delay(50, ct).ConfigureAwait(false);
        }
    }

    // Sets up the assistant block and a new input prompt below the submitted text.
    // Must only be called from the input loop (not under consoleLock).
    private void SetupAssistantLayout(string submittedInput)
    {
        lock (this.consoleLock)
        {
            // Overwrite the current input line with the submitted text in white.
            var lineWidth = GetLineWidth();
            SetCursorPositionSafe(0, this.inputRow);
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write(submittedInput + new string(' ', Math.Max(0, lineWidth - submittedInput.Length)));
            Console.ResetColor();

            this.assistantContentLines = 1;
            this.ReserveAssistantAndInputRegionLocked();
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
        this.inputRow = ClampRow(this.inputRow);
        if (this.inputRow < 3)
            return;

        var lineWidth = GetLineWidth();

        // Blank separator before user header (also clears any stale content when region expands).
        SetCursorPositionSafe(0, this.inputRow - 3);
        Console.Write(new string(' ', lineWidth));

        // "  user:" header in green.
        SetCursorPositionSafe(0, this.inputRow - 2);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("  user:" + new string(' ', Math.Max(0, lineWidth - 7)));
        Console.ResetColor();

        // Blank after user header.
        SetCursorPositionSafe(0, this.inputRow - 1);
        Console.Write(new string(' ', lineWidth));

        // Current input text in white.
        SetCursorPositionSafe(0, this.inputRow);
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write(this.currentInput + new string(' ', Math.Max(0, lineWidth - this.currentInput.Length)));
        Console.ResetColor();

        SetCursorPositionSafe(Math.Min(this.currentInput.Length, Console.BufferWidth - 1), this.inputRow);
    }

    private void OnTextChunkReceived(object? sender, string chunk)
    {
        string renderedText;
        lock (this.consoleLock)
        {
            this.assistantAccumulatedText.Append(chunk);
            renderedText = this.assistantAccumulatedText.ToString();
        }

        this.RenderAssistantChunk(renderedText);
    }

    private void OnTurnCompleted(object? sender, AgentChatHistoryItem item)
    {
        Interlocked.Increment(ref this.completedTurnCount);
        var itemText = string.Concat(item.Contents.OfType<TextContent>().Select(static content => content.Text));
        lock (this.consoleLock)
        {
            this.lastAccumulatedText = itemText;
            this.assistantAccumulatedText.Clear();
            this.assistantAccumulatedText.Append(this.lastAccumulatedText);
        }

        if (!this.supportsInteractiveRendering)
        {
            lock (this.consoleLock)
                Console.WriteLine($"assistant > {itemText}");
            this.ClearAssistantAccumulatedText();
            return;
        }

        this.FinalizeAssistantTurn();
        this.ClearAssistantAccumulatedText();
    }

    private void ClearAssistantAccumulatedText()
    {
        lock (this.consoleLock)
        {
            this.assistantAccumulatedText.Clear();
            this.lastAccumulatedText = string.Empty;
            this.currentLogLines.Clear();
        }
    }

    private void ResetAssistantStreamAfterInterrupt()
    {
        this.ClearAssistantAccumulatedText();

        if (!this.supportsInteractiveRendering)
        {
            return;
        }

        lock (this.consoleLock)
        {
            this.isStreaming = false;

            if (this.assistantBlockRow >= 0)
            {
                this.RenderAssistantContentLocked();
            }

            this.assistantBlockRow = -1;
            this.assistantContentLines = 0;
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
        var lineWidth = GetLineWidth();
        this.FlushPendingLogLinesLocked(lineWidth);
        this.assistantBlockRow = ClampRow(this.assistantBlockRow);
        this.inputRow = ClampRow(this.inputRow);

        // Split the raw content into display rows — no prefix; header is a separate row.
        var displayRows = GetDisplayRows(this.lastAccumulatedText, lineWidth);
        if (displayRows.Count == 0)
        {
            displayRows.Add(string.Empty);
        }
        var requiredLines = Math.Max(1, displayRows.Count);

        // Expand the assistant region downward if the response has grown.
        // Stop if we've hit the top of the buffer (assistantBlockRow can't go negative).
        while (requiredLines > this.assistantContentLines)
        {
            var prevInputRow = this.inputRow;
            SetCursorPositionSafe(0, this.inputRow);
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
        SetCursorPositionSafe(0, this.assistantBlockRow + 1);
        Console.ForegroundColor = ConsoleColor.Blue;
        var header = this.AssistantHeaderLine;
        Console.Write(header + new string(' ', Math.Max(0, lineWidth - header.Length)));
        Console.ResetColor();

        // Clear the content region.
        for (var i = 0; i < this.assistantContentLines; i++)
        {
            SetCursorPositionSafe(0, this.assistantBlockRow + 3 + i);
            Console.Write(new string(' ', lineWidth));
        }

        // Write content rows in white, showing the last assistantContentLines rows when the
        // content overflows the allocated region (sliding window).
        var skipRows = Math.Max(0, displayRows.Count - this.assistantContentLines);
        for (var i = 0; i < Math.Min(this.assistantContentLines, displayRows.Count); i++)
        {
            SetCursorPositionSafe(0, this.assistantBlockRow + 3 + i);
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write(displayRows[skipRows + i]);
        }
        Console.ResetColor();

        this.RedrawInputLine();
    }

    // Writes pending log lines above the assistant region, then re-anchors the
    // assistant/input block so the next render paints fresh assistant + user text.
    // Must be called under consoleLock.
    private void FlushPendingLogLinesLocked(int lineWidth)
    {
        if (this.currentLogLines.Count == 0 || this.assistantBlockRow < 0)
        {
            return;
        }

        var logRows = new List<string>();
        foreach (var line in this.currentLogLines)
        {
            logRows.AddRange(GetDisplayRows(line, lineWidth));
        }

        if (logRows.Count == 0)
        {
            this.currentLogLines.Clear();
            return;
        }

        this.ClearAssistantAndInputRegionLocked(lineWidth);

        SetCursorPositionSafe(0, this.assistantBlockRow);
        Console.ForegroundColor = ConsoleColor.DarkGray;
        foreach (var row in logRows)
        {
            Console.Write(row + new string(' ', Math.Max(0, lineWidth - row.Length)));
            Console.WriteLine();
        }
        Console.ResetColor();
        Console.WriteLine();

        this.currentLogLines.Clear();

        // Recreate assistant/user region directly beneath newly emitted logs.
        this.assistantBlockRow = Console.CursorTop;
        this.ReserveAssistantAndInputRegionLocked();
    }

    // Clears the currently allocated assistant content area and user prompt area.
    // Must be called under consoleLock.
    private void ClearAssistantAndInputRegionLocked(int lineWidth)
    {
        if (this.assistantBlockRow < 0)
        {
            return;
        }

        var top = ClampRow(this.assistantBlockRow);
        var bottom = ClampRow(this.inputRow);
        if (bottom < top)
        {
            (top, bottom) = (bottom, top);
        }

        for (var row = top; row <= bottom; row++)
        {
            SetCursorPositionSafe(0, row);
            Console.Write(new string(' ', lineWidth));
        }
    }

    // Reserves layout rows for assistant + user prompt sections and updates anchors.
    // Must be called under consoleLock.
    private void ReserveAssistantAndInputRegionLocked()
    {
        var rowsToReserve = this.assistantContentLines + 7;
        for (var i = 0; i < rowsToReserve; i++)
        {
            Console.WriteLine();
        }

        this.inputRow = ClampRow(Console.CursorTop);
        this.assistantBlockRow = ClampRow(this.inputRow - (this.assistantContentLines + 7));
    }

    private static int GetLineWidth() => Math.Max(1, Console.BufferWidth - 1);

    private static int ClampRow(int row) => Math.Clamp(row, 0, Math.Max(0, Console.BufferHeight - 1));

    private static void SetCursorPositionSafe(int left, int top)
    {
        var safeLeft = Math.Clamp(left, 0, Math.Max(0, Console.BufferWidth - 1));
        var safeTop = ClampRow(top);
        Console.SetCursorPosition(safeLeft, safeTop);
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

internal sealed class InteractiveConsoleLoggerProvider(Action<string> onLogLine) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new InteractiveConsoleLogger(categoryName, onLogLine);

    public void Dispose()
    {
    }
}

internal sealed class InteractiveConsoleLogger(string categoryName, Action<string> onLogLine) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Trace;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!this.IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var line = $"{DateTime.Now:HH:mm:ss} {logLevel.ToString().ToLowerInvariant(),-5}: {categoryName} {message}";
        if (exception is not null)
        {
            line = $"{line} {exception.GetType().Name}: {exception.Message}";
        }

        onLogLine(line);
    }
}

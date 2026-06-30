using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Dock.Model.Controls;
using Dock.Model.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Serialization;
using Phantom.Workspaces.Llm.Shell;
using Phantom.Workspaces.Llm.Trust;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Exposes the workspace-gui toolset: AI tools that allow agents to open/close workspace panes,
/// close tabs, and invoke entity shortcuts (e.g. Open, Delete) in the live workspace UI.
/// All tool invocations that touch the UI are dispatched on the UI thread.
/// </summary>
public sealed class WorkspaceGuiContextProvider : AIContextProvider
{
    private static readonly EntityName WorkspaceGuiToolInstructionsEntityName =
        new("documentation", "entity-workspace-gui-agent-tool-instructions");

    private readonly string stateKey = $"workspace-gui:{Guid.NewGuid():n}";
    private readonly WorkspaceGuiContext context;

    public WorkspaceGuiContextProvider(WorkspaceGuiContext context)
        : base(null, null, null)
    {
        this.context = context;
    }

    public override IReadOnlyList<string> StateKeys => [this.stateKey];

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext invokingContext,
        CancellationToken cancellationToken)
    {
        _ = invokingContext;
        var instructions = await this.GetInstructionsAsync(cancellationToken);
        return new AIContext
        {
            Instructions = instructions,
            Tools =
            [
                new WorkspaceListTool(this.context),
                new TabListTool(this.context),
                new WorkspaceCloseTool(this.context),
                new TabCloseTool(this.context),
                new EntityInvokeShortcutTool(this.context),
                new OpenTabTool(this.context),
            ],
        };
    }

    private async Task<string?> GetInstructionsAsync(CancellationToken cancellationToken)
    {
        var entities = await this.context.MainWindowViewModel.EntityBroker.GetEntitiesAsync(
            [new GetEntityRequest { EntityName = WorkspaceGuiToolInstructionsEntityName }],
            cancellationToken);
        return NoteEntityDocument.TryReadDefaultMarkdownText(entities.FirstOrDefault()?.Data);
    }

    private sealed class WorkspaceListTool : AIFunction
    {
        private static readonly JsonElement InputSchema = JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {},
              "additionalProperties": false
            }
            """).RootElement.Clone();

        private readonly WorkspaceGuiContext context;

        public WorkspaceListTool(WorkspaceGuiContext context)
        {
            this.context = context;
        }

        public override string Name => "workspace_list";

        public override string Description =>
            "List all open workspace panes. Returns an array of objects with workspace_entity_id, title, and is_selected for each pane.";

        public override JsonElement JsonSchema => InputSchema;

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            var result = Dispatcher.UIThread.Invoke(() =>
            {
                var mainVm = this.context.MainWindowViewModel;
                var selectedId = mainVm.SelectedWorkspacePane?.Id;
                return mainVm.WorkspacePanes
                    .Select(pane => new
                    {
                        workspace_entity_id = pane.Id,
                        title = pane.Title,
                        is_selected = string.Equals(pane.Id, selectedId, StringComparison.Ordinal),
                    })
                    .ToArray();
            });
            return new ValueTask<object?>(Serialize(result));
        }
    }

    private sealed class TabListTool : AIFunction
    {
        private static readonly JsonElement InputSchema = JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "workspace_entity_id": {
                  "type": "string",
                  "format": "uuid",
                  "description": "The workspace pane to enumerate tabs for. If omitted, uses the currently selected workspace pane."
                }
              },
              "additionalProperties": false
            }
            """).RootElement.Clone();

        private readonly WorkspaceGuiContext context;

        public TabListTool(WorkspaceGuiContext context)
        {
            this.context = context;
        }

        public override string Name => "tab_list";

        public override string Description =>
            "List open tabs in a workspace pane. If workspace_entity_id is omitted, uses the selected workspace pane. "
            + "Returns an array of objects with tab_id, title, and is_active for each tab.";

        public override JsonElement JsonSchema => InputSchema;

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            string? workspaceEntityId = null;
            if (arguments.TryGetValue("workspace_entity_id", out var idArg)
                && idArg is JsonElement idElement
                && idElement.ValueKind == JsonValueKind.String)
            {
                workspaceEntityId = idElement.GetString();
            }

            var capturedId = workspaceEntityId;
            var result = Dispatcher.UIThread.Invoke(() =>
            {
                var mainVm = this.context.MainWindowViewModel;
                WorkspacePaneViewModel? pane;
                if (capturedId is not null)
                {
                    pane = mainVm.WorkspacePanes
                        .FirstOrDefault(p => string.Equals(p.Id, capturedId, StringComparison.Ordinal));
                    if (pane is null)
                    {
                        return Serialize(new { error = $"Workspace pane '{capturedId}' not found." });
                    }
                }
                else
                {
                    pane = mainVm.SelectedWorkspacePane;
                }

                if (pane.ContentLayout is null)
                {
                    return Serialize(Array.Empty<object>());
                }

                var documentDock = FindDocumentDock(pane.ContentLayout);
                if (documentDock?.VisibleDockables is null)
                {
                    return Serialize(Array.Empty<object>());
                }

                var activeTabId = documentDock.ActiveDockable?.Id;
                var tabs = documentDock.VisibleDockables
                    .OfType<WorkspaceDocument>()
                    .Select(doc => new
                    {
                        tab_id = doc.Id,
                        title = doc.TabViewModel.Title,
                        is_active = string.Equals(doc.Id, activeTabId, StringComparison.Ordinal),
                    })
                    .ToArray();
                return Serialize(tabs);
            });
            return new ValueTask<object?>(result);
        }
    }

    private static IDocumentDock? FindDocumentDock(IDockable dockable)
    {
        if (dockable is IDocumentDock documentDock)
        {
            return documentDock;
        }

        if (dockable is IDock dock && dock.VisibleDockables is not null)
        {
            foreach (var child in dock.VisibleDockables)
            {
                var result = FindDocumentDock(child);
                if (result is not null)
                {
                    return result;
                }
            }
        }

        return null;
    }

    private sealed class WorkspaceCloseTool : AIFunction
    {
        private static readonly JsonElement InputSchema = JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "workspace_entity_id": {
                  "type": "string",
                  "format": "uuid",
                  "description": "The entity-id of the workspace pane to close."
                }
              },
              "required": ["workspace_entity_id"],
              "additionalProperties": false
            }
            """).RootElement.Clone();

        private readonly WorkspaceGuiContext context;

        public WorkspaceCloseTool(WorkspaceGuiContext context)
        {
            this.context = context;
        }

        public override string Name => "workspace_close";

        public override string Description =>
            "Close an open workspace pane by workspace entity-id. No-ops if the pane is not found or is the default placeholder pane.";

        public override JsonElement JsonSchema => InputSchema;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            if (!arguments.TryGetValue("workspace_entity_id", out var idArg)
                || idArg is not JsonElement idElement
                || idElement.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(idElement.GetString()))
            {
                return Serialize(new { error = "workspace_entity_id is required." });
            }

            var workspaceEntityId = idElement.GetString()!;
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var pane = this.context.MainWindowViewModel.WorkspacePanes
                    .FirstOrDefault(p => string.Equals(p.Id, workspaceEntityId, StringComparison.Ordinal));
                if (pane is not null)
                {
                    await this.context.MainWindowViewModel.RemoveWorkspacePaneAsync(pane);
                }
            });

            return Serialize(new { closed = true });
        }
    }

    private sealed class TabCloseTool : AIFunction
    {
        private static readonly JsonElement InputSchema = JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "tab_id": {
                  "type": "string",
                  "description": "The tab id (entity-id) used to identify the tab to close."
                }
              },
              "required": ["tab_id"],
              "additionalProperties": false
            }
            """).RootElement.Clone();

        private readonly WorkspaceGuiContext context;

        public TabCloseTool(WorkspaceGuiContext context)
        {
            this.context = context;
        }

        public override string Name => "tab_close";

        public override string Description =>
            "Close an open tab by tab id (the entity-id used to open it). No-ops if not found.";

        public override JsonElement JsonSchema => InputSchema;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            if (!arguments.TryGetValue("tab_id", out var idArg)
                || idArg is not JsonElement idElement
                || idElement.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(idElement.GetString()))
            {
                return Serialize(new { error = "tab_id is required." });
            }

            var tabId = idElement.GetString()!;
            var closed = await Dispatcher.UIThread.InvokeAsync(
                () => this.context.MainWindowViewModel.CloseTabById(tabId));

            return Serialize(new { closed });
        }
    }

    private sealed class EntityInvokeShortcutTool : AIFunction
    {
        private static readonly JsonElement InputSchema = JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "entity_id": {
                  "type": "string",
                  "format": "uuid",
                  "description": "The entity-id of the entity on which to invoke the shortcut."
                },
                "shortcut": {
                  "type": "string",
                  "enum": ["Open", "Json", "Delete", "StartAgentSession", "StartShell"],
                  "description": "The shortcut to invoke. 'Open' opens the entity as a workspace pane, tab, or agent chat session — use this for all open/navigate operations."
                }
              },
              "required": ["entity_id", "shortcut"],
              "additionalProperties": false
            }
            """).RootElement.Clone();

        private readonly WorkspaceGuiContext context;

        public EntityInvokeShortcutTool(WorkspaceGuiContext context)
        {
            this.context = context;
        }

        public override string Name => "entity_invoke_shortcut";

        public override string Description =>
            "Invoke a named shortcut on an entity by entity-id. "
            + "Shortcuts: 'Open' opens the entity (workspace pane, tab, agent chat session — use this for all open/navigate operations), "
            + "'Json' toggles raw JSON view, 'Delete' deletes the entity, "
            + "'StartAgentSession' starts an agent session on the entity's profile, "
            + "'StartShell' starts a shell on the entity's profile. "
            + "Opening anything navigates to it and pushes a navigation history entry so the user can Ctrl+\u2212 back.";

        public override JsonElement JsonSchema => InputSchema;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            if (!arguments.TryGetValue("entity_id", out var entityIdArg)
                || entityIdArg is not JsonElement entityIdElement
                || entityIdElement.ValueKind != JsonValueKind.String
                || !Guid.TryParse(entityIdElement.GetString(), out var entityIdValue))
            {
                return Serialize(new { error = "entity_id must be a valid UUID." });
            }

            if (!arguments.TryGetValue("shortcut", out var shortcutArg)
                || shortcutArg is not JsonElement shortcutElement
                || shortcutElement.ValueKind != JsonValueKind.String)
            {
                return Serialize(new { error = "shortcut is required." });
            }

            var shortcutName = shortcutElement.GetString()!;
            var shortcut = ResolveShortcut(shortcutName);
            if (shortcut is null)
            {
                return Serialize(new
                {
                    error = $"Unknown shortcut '{shortcutName}'. Valid values: Open, Json, Delete, StartAgentSession, StartShell.",
                });
            }

            var entityId = new EntityId(entityIdValue);
            var handled = await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var entities = await this.context.MainWindowViewModel.EntityBroker.GetEntitiesAsync([entityId]);
                var entity = entities.FirstOrDefault();
                if (entity is null)
                {
                    return false;
                }

                return await this.context.ShortcutManager.HandleShortcutAsync(
                    this.context.MainWindowViewModel,
                    shortcut,
                    entity);
            });

            return Serialize(new { handled });
        }

        private static Shortcut? ResolveShortcut(string name) => name switch
        {
            "Open" => Shortcut.Open,
            "Json" => Shortcut.Json,
            "Delete" => Shortcut.Delete,
            "StartAgentSession" => Shortcut.StartAgentSession,
            "StartShell" => Shortcut.StartShell,
            _ => null,
        };
    }

    private sealed class OpenTabTool : AIFunction
    {
        private static readonly JsonElement InputSchema = JsonDocument.Parse(
            """
            {
              "type": "object",
              "required": ["target"],
              "additionalProperties": false,
              "properties": {
                "target": { "type": "string", "enum": ["entity", "url", "shell"], "description": "What to open." },
                "entity_id": { "type": "string", "description": "UUID of the entity to open (required when target=entity)." },
                "url": { "type": "string", "description": "URL to load (required when target=url)." },
                "title": { "type": "string", "description": "Optional display title for the tab." },
                "command": { "type": "string", "description": "Executable to run (required when target=shell)." },
                "working_directory": { "type": "string", "description": "Working directory for the shell process." },
                "arguments": { "type": "array", "items": { "type": "string" }, "description": "Arguments to pass to the command." },
                "focus": { "type": "boolean", "description": "Whether to focus the new tab (default: true)." }
              }
            }
            """).RootElement.Clone();

        private readonly WorkspaceGuiContext context;

        public OpenTabTool(WorkspaceGuiContext context)
        {
            this.context = context;
        }

        public override string Name => "open_tab";

        public override string Description =>
            "Open a new tab in the workspace. "
            + "Supports three targets: 'entity' (open an entity by UUID), "
            + "'url' (open an ephemeral browser tab), "
            + "'shell' (open an ephemeral shell tab). "
            + "Returns { tab_id } on success or an error string on failure.";

        public override JsonElement JsonSchema => InputSchema;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            if (!arguments.TryGetValue("target", out var targetArg)
                || targetArg is not JsonElement targetEl
                || targetEl.ValueKind != JsonValueKind.String)
            {
                return Serialize(new { error = "target is required." });
            }

            var target = targetEl.GetString()!;
            var focus = ParseFocus(arguments);

            return target switch
            {
                "entity" => await this.OpenEntityTabAsync(arguments, focus, cancellationToken),
                "url" => await this.OpenUrlTabAsync(arguments, focus, cancellationToken),
                "shell" => await this.OpenShellTabAsync(arguments, focus, cancellationToken),
                _ => Serialize(new { error = $"Unknown target '{target}'. Valid values: entity, url, shell." }),
            };
        }

        private async ValueTask<object?> OpenEntityTabAsync(
            AIFunctionArguments arguments,
            bool focus,
            CancellationToken cancellationToken)
        {
            if (!arguments.TryGetValue("entity_id", out var idArg)
                || idArg is not JsonElement idEl
                || idEl.ValueKind != JsonValueKind.String
                || !Guid.TryParse(idEl.GetString(), out var guid))
            {
                return Serialize(new { error = "entity_id must be a valid UUID when target=entity." });
            }

            var tabId = guid.ToString();
            var entityId = new EntityId(guid);

            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                await this.context.MainWindowViewModel.OpenEntityTabAsync(
                    new GetEntityRequest { EntityId = entityId },
                    focus: focus);
            });

            return Serialize(new { tab_id = tabId });
        }

        private async ValueTask<object?> OpenUrlTabAsync(
            AIFunctionArguments arguments,
            bool focus,
            CancellationToken cancellationToken)
        {
            if (!arguments.TryGetValue("url", out var urlArg)
                || urlArg is not JsonElement urlEl
                || urlEl.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(urlEl.GetString()))
            {
                return Serialize(new { error = "url is required when target=url." });
            }

            var url = urlEl.GetString()!;
            var title = GetOptionalString(arguments, "title") ?? url;
            var tabId = $"web-{Guid.NewGuid():N}";

            var tab = new WebViewModel(url, this.context.MainWindowViewModel)
            {
                Id = tabId,
                Title = title,
            };

            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                await this.context.MainWindowViewModel.OpenTabAsync(tab, focus: focus);
            });

            return Serialize(new { tab_id = tabId });
        }

        private async ValueTask<object?> OpenShellTabAsync(
            AIFunctionArguments arguments,
            bool focus,
            CancellationToken cancellationToken)
        {
            if (!arguments.TryGetValue("command", out var commandArg)
                || commandArg is not JsonElement commandEl
                || commandEl.ValueKind != JsonValueKind.String
                || string.IsNullOrEmpty(commandEl.GetString()))
            {
                return Serialize(new { error = "command is required when target=shell." });
            }

            var command = commandEl.GetString()!;
            var workingDirectory = GetOptionalString(arguments, "working_directory");
            var shellArguments = ParseStringArray(arguments, "arguments");
            var title = GetOptionalString(arguments, "title") ?? command;
            var tabId = $"shell-{Guid.NewGuid():N}";

            ITerminalSession session;
            try
            {
                session = await OpenShellSessionAsync(command, shellArguments, workingDirectory, cancellationToken);
            }
            catch (Exception ex)
            {
                return Serialize(new { error = $"Failed to open shell session: {ex.Message}" });
            }

            var tab = new ShellTabViewModel(session)
            {
                Id = tabId,
                Title = title,
            };

            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                await this.context.MainWindowViewModel.OpenTabAsync(tab, focus: focus);
            });

            return Serialize(new { tab_id = tabId });
        }

        private Task<ITerminalSession> OpenShellSessionAsync(
            string command,
            IReadOnlyList<string> shellArguments,
            string? workingDirectory,
            CancellationToken cancellationToken)
        {
            if (this.context.EphemeralShellSessionOpener is not null)
            {
                return this.context.EphemeralShellSessionOpener(command, shellArguments, workingDirectory, cancellationToken);
            }

            var payloadJson = BuildShellPayloadJson(command, shellArguments, workingDirectory);
            using var payloadDocument = JsonDocument.Parse(payloadJson);
            var request = new TrustedStreamRequest
            {
                TargetClientInstance = TrustProfile.LocalClientInstance,
                StreamKind = "shell",
                OpenPayload = payloadDocument.RootElement.Clone(),
            };

            return OpenLocalSessionAsync(request, cancellationToken);
        }

        private static string BuildShellPayloadJson(
            string command,
            IReadOnlyList<string> shellArguments,
            string? workingDirectory)
        {
            var argsJson = shellArguments.Count == 0
                ? "[]"
                : "[" + string.Join(",", shellArguments.Select(a => JsonSerializer.Serialize(a))) + "]";

            var wdJson = workingDirectory is null
                ? "null"
                : JsonSerializer.Serialize(workingDirectory);

            return $$"""
                {
                  "mode": "pty",
                  "command": {{JsonSerializer.Serialize(command)}},
                  "command-arguments": {{argsJson}},
                  "working-directory": {{wdJson}}
                }
                """;
        }

        private static async Task<ITerminalSession> OpenLocalSessionAsync(
            TrustedStreamRequest request,
            CancellationToken ct)
        {
            var executor = new LocalTrustedExecutor();
            var stream = await executor.OpenStreamAsync(request, ct);
            return new StreamTerminalSession(stream);
        }

        private static bool ParseFocus(AIFunctionArguments arguments)
        {
            if (arguments.TryGetValue("focus", out var focusArg)
                && focusArg is JsonElement focusEl
                && focusEl.ValueKind == JsonValueKind.False)
            {
                return false;
            }

            return true;
        }

        private static string? GetOptionalString(AIFunctionArguments arguments, string key)
        {
            if (arguments.TryGetValue(key, out var arg)
                && arg is JsonElement el
                && el.ValueKind == JsonValueKind.String)
            {
                return el.GetString();
            }

            return null;
        }

        private static IReadOnlyList<string> ParseStringArray(AIFunctionArguments arguments, string key)
        {
            if (!arguments.TryGetValue(key, out var arg)
                || arg is not JsonElement el
                || el.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var result = new List<string>();
            foreach (var item in el.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
                {
                    result.Add(s);
                }
            }

            return result;
        }

        /// <summary>
        /// A thin <see cref="ITerminalSession"/> over a <see cref="StreamMessageChannelStream"/>
        /// returned by <see cref="LocalTrustedExecutor.OpenStreamAsync"/>.
        /// </summary>
        private sealed class StreamTerminalSession : ITerminalSession
        {
            private readonly StreamMessageChannelStream channelStream;

            public StreamTerminalSession(Stream stream)
            {
                this.channelStream = (StreamMessageChannelStream)stream;
            }

            public Stream Stream => this.channelStream;

            public ValueTask ResizeAsync(int columns, int rows, CancellationToken ct)
                => this.channelStream.SendControlAsync(
                    new StreamControlMessage
                    {
                        Type = StreamControlMessage.Types.Resize,
                        Columns = columns,
                        Rows = rows,
                    },
                    ct);

            public ValueTask SignalAsync(string signal, CancellationToken ct)
                => this.channelStream.SendControlAsync(
                    new StreamControlMessage
                    {
                        Type = StreamControlMessage.Types.Signal,
                        Signal = signal,
                    },
                    ct);

            public Task<int> WaitForExitAsync()
                => this.channelStream.Completion.ContinueWith(
                    static _ => 0,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

            public ValueTask DisposeAsync() => this.channelStream.DisposeAsync();
        }
    }

    private static JsonElement Serialize(object value)
    {
        return JsonSerializer.SerializeToElement(value);
    }
}

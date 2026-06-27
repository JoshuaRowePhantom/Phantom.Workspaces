using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Dock.Model.Controls;
using Dock.Model.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Exposes the workspace-gui toolset: AI tools that allow agents to open/close workspace panes,
/// close tabs, and invoke entity shortcuts (e.g. Open, Delete) in the live workspace UI.
/// All tool invocations that touch the UI are dispatched on the UI thread.
/// </summary>
public sealed class WorkspaceGuiContextProvider : AIContextProvider
{
    private readonly string stateKey = $"workspace-gui:{Guid.NewGuid():n}";
    private readonly WorkspaceGuiContext context;

    public WorkspaceGuiContextProvider(WorkspaceGuiContext context)
        : base(null, null, null)
    {
        this.context = context;
    }

    public override IReadOnlyList<string> StateKeys => [this.stateKey];

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext invokingContext,
        CancellationToken cancellationToken)
    {
        _ = invokingContext;
        _ = cancellationToken;
        return new ValueTask<AIContext>(new AIContext
        {
            Tools =
            [
                new WorkspaceListTool(this.context),
                new TabListTool(this.context),
                new WorkspaceCloseTool(this.context),
                new TabCloseTool(this.context),
                new EntityInvokeShortcutTool(this.context),
            ],
        });
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

    private static JsonElement Serialize(object value)
    {
        return JsonSerializer.SerializeToElement(value);
    }
}

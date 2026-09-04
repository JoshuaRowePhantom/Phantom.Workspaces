using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Gui.Shared.ViewModels;
using Phantom.Workspaces.Llm.Shell;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// A workspace tab bound to a live shell session. Wraps an <see cref="ITerminalSession"/> and
/// exposes a <see cref="TerminalSessionViewModel"/> for the terminal control to bind to. When
/// constructed with a source entity id + writer, the tab surfaces a control bar with
/// Stop/Restart/inline-command-line/Settings/Save.
/// </summary>
public sealed class ShellTabViewModel : WorkspaceTabViewModel
{
    private readonly Func<ShellEntityOpenSpec, CancellationToken, Task<ITerminalSession>>? sessionFactory;
    private readonly Func<UpdateRequest, CancellationToken, Task<UpdateResult>>? entityWriter;
    private readonly Func<ShellSettingsDialogViewModel, Task<ShellEntityOpenSpec?>>? dialogOpener;
    private readonly EntityId? sourceEntityId;
    private ITerminalSession session;
    private ShellEntityOpenSpec spec;
    private TerminalSessionViewModel terminalSession;
    private string commandLine;
    private ConcurrencyTag? concurrencyTag;
    private JsonElement? sourceEntityData;

    /// <summary>Simple constructor for ephemeral shells (no entity / no save path).</summary>
    public ShellTabViewModel(ITerminalSession session)
        : this(session, MakeDefaultSpec(session), null, null, null, null, null, null)
    {
    }

    /// <summary>Full constructor used by <see cref="OpenShellEntityShortcutHandler"/>.</summary>
    public ShellTabViewModel(
        ITerminalSession session,
        ShellEntityOpenSpec spec,
        Func<ShellEntityOpenSpec, CancellationToken, Task<ITerminalSession>>? sessionFactory,
        EntityId? sourceEntityId,
        ConcurrencyTag? concurrencyTag,
        JsonElement? sourceEntityData,
        Func<UpdateRequest, CancellationToken, Task<UpdateResult>>? entityWriter,
        Func<ShellSettingsDialogViewModel, Task<ShellEntityOpenSpec?>>? dialogOpener)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(spec);

        this.session = session;
        this.spec = spec;
        this.commandLine = spec.Command;
        this.sessionFactory = sessionFactory;
        this.sourceEntityId = sourceEntityId;
        this.concurrencyTag = concurrencyTag;
        this.sourceEntityData = sourceEntityData;
        this.entityWriter = entityWriter;
        this.dialogOpener = dialogOpener;
        this.terminalSession = MakeTerminalSession(session);

        this.StopCommand = new AsyncRelayCommand(_ => this.StopAsync());
        this.RestartCommand = new AsyncRelayCommand(
            _ => this.RestartAsync(),
            _ => this.sessionFactory is not null);
        this.SaveCommand = new AsyncRelayCommand(
            _ => this.SaveAsync(),
            _ => this.CanSave);
        this.OpenSettingsCommand = new AsyncRelayCommand(
            _ => this.OpenSettingsAsync(),
            _ => this.dialogOpener is not null);
    }

    /// <summary>The view model the terminal control binds to.</summary>
    public TerminalSessionViewModel TerminalSession
    {
        get => this.terminalSession;
        private set => this.SetProperty(ref this.terminalSession, value);
    }

    /// <summary>The current editable shell configuration for the tab (inline + dialog share this).</summary>
    public ShellEntityOpenSpec Spec
    {
        get => this.spec;
        private set
        {
            if (this.SetProperty(ref this.spec, value))
            {
                this.RaisePropertyChanged(nameof(this.Arguments));
                this.RaisePropertyChanged(nameof(this.WorkingDirectory));
            }
        }
    }

    /// <summary>Inline command-line text. Two-way bound to the top control bar and mirrored into <see cref="Spec"/>.</summary>
    public string CommandLine
    {
        get => this.commandLine;
        set
        {
            if (this.SetProperty(ref this.commandLine, value))
            {
                this.Spec = this.Spec with { Command = value };
            }
        }
    }

    /// <summary>The shell command arguments, surfaced as a space-joined string for the tab tooltip.</summary>
    public string Arguments => string.Join(" ", this.Spec.CommandArguments);

    /// <summary>The shell working directory (may be null), surfaced for the tab tooltip.</summary>
    public string? WorkingDirectory => this.Spec.WorkingDirectory;

    /// <summary>Signals the running shell process (graceful stop). Keeps the tab/stream alive.</summary>
    public AsyncRelayCommand StopCommand { get; }

    /// <summary>Disposes the current session and re-launches with the current <see cref="Spec"/>.</summary>
    public AsyncRelayCommand RestartCommand { get; }

    /// <summary>Persists the current configuration back to the source <c>shell</c> entity.</summary>
    public AsyncRelayCommand SaveCommand { get; }

    /// <summary>Opens the details dialog for full-shell configuration editing.</summary>
    public AsyncRelayCommand OpenSettingsCommand { get; }

    /// <summary>Whether <see cref="SaveCommand"/> can persist configuration back to a source entity.</summary>
    public bool CanSave => this.entityWriter is not null && this.sourceEntityId is not null;

    private static ShellEntityOpenSpec MakeDefaultSpec(ITerminalSession session)
    {
        _ = session;
        return new ShellEntityOpenSpec { Mode = "pty", Command = string.Empty };
    }

    private static TerminalSessionViewModel MakeTerminalSession(ITerminalSession s) => new()
    {
        Stream = s.Stream,
        ResizeCallback = (c, r, ct) => s.ResizeAsync(c, r, ct),
    };

    private async Task StopAsync()
    {
        await this.session.SignalAsync("SIGTERM", CancellationToken.None);
    }

    private async Task RestartAsync()
    {
        if (this.sessionFactory is null)
        {
            return;
        }

        var oldSession = this.session;
        var newSession = await this.sessionFactory(this.Spec, CancellationToken.None);
        this.session = newSession;
        this.TerminalSession = MakeTerminalSession(newSession);
        await oldSession.DisposeAsync();
    }

    private async Task SaveAsync()
    {
        if (this.entityWriter is null || this.sourceEntityId is null)
        {
            return;
        }

        var payload = BuildShellPayload(this.sourceEntityData, this.Spec);
        var request = new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata
            {
                Comment = new Markdown { Text = "Update shell configuration." },
            },
            Changes =
            [
                new EntityChange
                {
                    EntityId = this.sourceEntityId.Value,
                    ConcurrencyTag = this.concurrencyTag,
                    EntityChangeMode = EntityChangeMode.Replace,
                    Data = payload,
                },
            ],
        };

        var result = await this.entityWriter(request, CancellationToken.None);

        var updated = result.EntityResults.FirstOrDefault(
            r => r.RequestedEntityId == this.sourceEntityId.Value);
        if (updated?.CurrentEntity is EntitySnapshot snap)
        {
            this.concurrencyTag = snap.ConcurrencyTag;
            this.sourceEntityData = snap.Data;
        }
    }

    private async Task OpenSettingsAsync()
    {
        if (this.dialogOpener is null)
        {
            return;
        }

        var dialogVm = new ShellSettingsDialogViewModel(
            this.Spec,
            this.sourceEntityId,
            this.concurrencyTag,
            this.sourceEntityData,
            this.entityWriter);

        var result = await this.dialogOpener(dialogVm);
        if (result is not null)
        {
            this.Spec = result;
            this.CommandLine = result.Command;
        }
    }

    /// <summary>
    /// Merges the current <paramref name="spec"/> into the entity's existing <paramref name="existing"/>
    /// data element (preserving unrelated fields like <c>entity-types</c> and <c>display-name</c>).
    /// Fields not present in the spec are omitted from the merged output.
    /// </summary>
    internal static JsonElement BuildShellPayload(JsonElement? existing, ShellEntityOpenSpec spec)
    {
        JsonObject root;
        if (existing is JsonElement e && e.ValueKind == JsonValueKind.Object)
        {
            root = (JsonObject)JsonNode.Parse(e.GetRawText())!;
        }
        else
        {
            root = new JsonObject();
        }

        root["mode"] = spec.Mode;
        root["command"] = spec.Command;

        var argsArray = new JsonArray();
        foreach (var arg in spec.CommandArguments)
        {
            argsArray.Add(arg);
        }
        root["command-arguments"] = argsArray;

        if (spec.WorkingDirectory is not null)
        {
            root["working-directory"] = spec.WorkingDirectory;
        }
        else
        {
            root.Remove("working-directory");
        }

        var envObject = new JsonObject();
        if (spec.Environment is not null)
        {
            foreach (var kvp in spec.Environment)
            {
                envObject[kvp.Key] = kvp.Value;
            }
        }
        root["environment"] = envObject;

        using var doc = JsonDocument.Parse(root.ToJsonString());
        return doc.RootElement.Clone();
    }

    public override async ValueTask DisposeAsync()
    {
        await this.session.DisposeAsync();
        await base.DisposeAsync();
    }
}

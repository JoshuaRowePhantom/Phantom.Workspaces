using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Phantom.Workspaces.Services.Access;

namespace Phantom.Workspaces.Controls;

internal sealed class SavedCredentialsAdministrationControl : UserControl, IAsyncDisposable
{
    private readonly SavedCredentialAdministration administration;
    private readonly StackPanel rows = new() { Spacing = 6 };
    private readonly StackPanel editor = new() { Spacing = 6, IsVisible = false };
    private readonly TextBox name = new();
    private readonly SensitiveCredentialTextBox value = new() { PasswordChar = '●', MaxLength = 1280 };
    private readonly TextBlock status = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly Button reveal = new() { Content = "Reveal value" };
    private readonly Button save = new() { Content = "Save changes" };
    private readonly Button delete = new() { Content = "Confirm delete saved credential" };
    private readonly DispatcherTimer revealTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? editorLifetime;
    private readonly List<Button> actions = [];
    private SavedCredentialMetadata? selected;
    private EditorMode mode;
    private int revision;
    private bool disposed;
    private Task? active;

    internal SavedCredentialsAdministrationControl(
        SavedCredentialAdministration administration, Control cachedCredentials)
    {
        this.administration = administration;
        AutomationProperties.SetName(this.name, "Saved credential name");
        AutomationProperties.SetName(this.value, "Saved credential value");
        AutomationProperties.SetLiveSetting(this.status, AutomationLiveSetting.Polite);
        this.revealTimer.Tick += (_, _) => this.Remask();
        this.LostFocus += (_, _) => this.Remask();
        this.reveal.Click += async (_, _) => await this.RevealAsync();
        this.save.Click += async (_, _) => await this.SaveAsync();
        this.delete.Click += async (_, _) => await this.DeleteAsync();
        var cancel = new Button { Content = "Cancel / close editor" };
        cancel.Click += (_, _) => this.CloseEditor();
        this.editor.Children.Add(new TextBlock
        {
            Text = "Values stay in the platform secret store. Save intentionally overwrites this exact name. " +
                "External editors/processes are last-writer-wins; changes cannot be checked atomically.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        this.editor.Children.Add(this.name);
        this.editor.Children.Add(this.value);
        this.editor.Children.Add(this.reveal);
        this.editor.Children.Add(this.save);
        this.editor.Children.Add(this.delete);
        this.editor.Children.Add(cancel);
        var refresh = new Button { Content = "Refresh saved named credentials" };
        refresh.Click += async (_, _) => await this.RefreshAsync();
        var add = new Button { Content = "Add credential", IsEnabled = administration.IsSupported };
        add.Click += (_, _) => this.OpenAdd();
        this.actions.Add(add);
        this.actions.Add(refresh);
        this.Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "Saved named credentials", FontWeight = Avalonia.Media.FontWeight.Bold },
                    new TextBlock
                    {
                        Text = administration.IsSupported
                            ? "View, edit or add named secrets. Selecting or refreshing never reads values."
                            : "Named saved-credential management is unavailable on this platform.",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    },
                    refresh, add, this.rows, this.editor, this.status,
                    new TextBlock { Text = "Cached / provider-managed credentials", FontWeight = Avalonia.Media.FontWeight.Bold },
                    cachedCredentials,
                },
            },
        };
        this.DetachedFromVisualTree += (_, _) => this.CloseEditor();
    }

    internal string SafeStatus => this.status.Text ?? "";
    internal SensitiveCredentialTextBox ValueField => this.value;
    internal TextBox NameField => this.name;
    internal bool IsEditorVisible => this.editor.IsVisible;
    internal IReadOnlyList<SavedCredentialMetadata> Items { get; private set; } = [];

    internal Task RefreshAsync() => this.RunAsync(this.RefreshCoreAsync);

    private async Task RefreshCoreAsync(CancellationToken ct)
    {
        var items = await this.administration.ListAsync(ct);
        ct.ThrowIfCancellationRequested();
        this.Items = items;
        this.rows.Children.Clear();
        foreach (var item in items)
        {
            var row = new StackPanel { Spacing = 4 };
            row.Children.Add(new TextBlock
            {
                Text = item.CanManage ? item.Name : $"Unsupported/provider-managed record ({item.Identifier.ToString("N")[..8]})",
            });
            row.Children.Add(new TextBlock { Text = item.Classification });
            if (item.CanManage)
            {
                var view = new Button { Content = "View", CommandParameter = item };
                var edit = new Button { Content = "Edit value", CommandParameter = item };
                var remove = new Button { Content = "Delete saved credential", CommandParameter = item };
                AutomationProperties.SetName(view, $"View saved credential {item.Name}");
                AutomationProperties.SetName(edit, $"Edit value of saved credential {item.Name}");
                AutomationProperties.SetName(remove, $"Delete saved credential {item.Name}");
                view.Click += async (_, _) => await this.OpenViewAsync(item);
                edit.Click += async (_, _) => await this.OpenEditAsync(item);
                remove.Click += (_, _) => this.OpenDelete(item);
                row.Children.Add(new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6,
                    Children = { view, edit, remove } });
            }
            this.rows.Children.Add(row);
        }
        this.status.Text = "Saved named credential list updated.";
    }

    internal Task OpenViewAsync(SavedCredentialMetadata row)
    {
        this.Open(row, EditorMode.View);
        return Task.CompletedTask;
    }

    internal Task OpenEditAsync(SavedCredentialMetadata row)
    {
        this.Open(row, EditorMode.Edit);
        return this.ReadSelectedAsync(reveal: false);
    }

    internal void OpenAdd() => this.Open(null, EditorMode.Add);
    internal void OpenDelete(SavedCredentialMetadata row) => this.Open(row, EditorMode.Delete);

    private void Open(SavedCredentialMetadata? row, EditorMode requestedMode)
    {
        this.CloseEditor();
        if (this.disposed || !this.administration.IsSupported || (row is not null && !this.Items.Contains(row))) return;
        this.editorLifetime = CancellationTokenSource.CreateLinkedTokenSource(this.lifetime.Token);
        this.selected = row;
        this.mode = requestedMode;
        this.name.Text = row?.Name ?? "";
        this.name.IsReadOnly = requestedMode != EditorMode.Add;
        this.value.IsVisible = requestedMode != EditorMode.Delete;
        this.value.IsReadOnly = requestedMode == EditorMode.View;
        this.reveal.IsVisible = requestedMode == EditorMode.View;
        this.save.IsVisible = requestedMode is EditorMode.Add or EditorMode.Edit;
        this.save.Content = requestedMode == EditorMode.Add ? "Add credential" : "Save changes";
        this.delete.IsVisible = requestedMode == EditorMode.Delete;
        this.status.Text = requestedMode == EditorMode.Delete
            ? "Delete only this saved entry. Future acquisitions using its name will fail; approvals and provider access are unchanged."
            : "Value is masked. Reveal is an explicit local action; Cancel makes no changes.";
        this.editor.IsVisible = true;
    }

    internal Task RevealAsync() => this.ReadSelectedAsync(reveal: true);

    private Task ReadSelectedAsync(bool reveal)
    {
        var row = this.selected;
        var version = this.revision;
        if (row is null || this.mode is not (EditorMode.View or EditorMode.Edit)) return Task.CompletedTask;
        return this.RunAsync(async ct =>
        {
            using var material = await this.administration.ReadForEditorAsync(row, ct);
            ct.ThrowIfCancellationRequested();
            if (this.disposed || version != this.revision) return;
            var pointer = Marshal.SecureStringToBSTR(material!);
            try { this.value.Text = Marshal.PtrToStringBSTR(pointer); }
            finally { Marshal.ZeroFreeBSTR(pointer); }
            this.value.RevealPassword = reveal;
            if (reveal) this.revealTimer.Start();
            this.status.Text = reveal ? "Value revealed locally until focus loss or 30 seconds." : "Value loaded into masked editor.";
        }, this.editorLifetime?.Token ?? this.lifetime.Token);
    }

    internal Task SaveAsync()
    {
        if (this.mode is not (EditorMode.Add or EditorMode.Edit) || !this.editor.IsVisible)
            return Task.CompletedTask;
        var row = this.selected;
        var exactName = this.name.Text ?? "";
        var material = new SecureString();
        foreach (var character in this.value.Text ?? "") material.AppendChar(character);
        material.MakeReadOnly();
        return this.SaveOwnedAsync(row, exactName, material, this.revision);
    }

    private async Task SaveOwnedAsync(SavedCredentialMetadata? row, string exactName, SecureString material, int version)
    {
        using (material)
            await this.RunAsync(async ct =>
            {
                if (row is null) await this.administration.CreateAsync(exactName, material, ct);
                else await this.administration.UpdateAsync(row, material, ct);
                ct.ThrowIfCancellationRequested();
                if (version == this.revision)
                {
                    this.CloseEditor();
                    await this.RefreshCoreAsync(this.lifetime.Token);
                    this.status.Text = "Saved value updated. Approval and cache lifecycle are unchanged.";
                }
            }, this.editorLifetime?.Token ?? this.lifetime.Token);
    }

    internal Task DeleteAsync()
    {
        var row = this.selected;
        if (row is null || this.mode != EditorMode.Delete) return Task.CompletedTask;
        return this.RunAsync(async ct =>
        {
            await this.administration.DeleteAsync(row, ct);
            ct.ThrowIfCancellationRequested();
            this.CloseEditor();
            await this.RefreshCoreAsync(this.lifetime.Token);
            this.status.Text = "Saved credential deleted. Approval and provider access are unchanged.";
        }, this.editorLifetime?.Token ?? this.lifetime.Token);
    }

    internal void Remask()
    {
        this.revealTimer.Stop();
        this.value.RevealPassword = false;
        if (this.mode == EditorMode.View) this.value.Text = null;
    }

    internal void CloseEditor()
    {
        this.revision++;
        this.editorLifetime?.Cancel();
        this.editorLifetime?.Dispose();
        this.editorLifetime = null;
        this.Remask();
        this.value.Text = null;
        this.name.Text = null;
        this.selected = null;
        this.editor.IsVisible = false;
    }

    private Task RunAsync(Func<CancellationToken, Task> operation, CancellationToken ct = default)
    {
        if (this.disposed || this.active is { IsCompleted: false }) return Task.CompletedTask;
        return this.active = this.ExecuteAsync(operation, ct);
    }

    private async Task ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, this.lifetime.Token);
        this.SetBusy(true);
        try { await operation(linked.Token); }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        { if (!this.disposed) this.status.Text = "Operation cancelled."; }
        catch (SavedCredentialOperationException exception)
        { if (!this.disposed) this.status.Text = exception.Message; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or PlatformNotSupportedException)
        { if (!this.disposed) this.status.Text = "Saved credential operation unavailable. No success was recorded; retry or cancel."; }
        finally { if (!this.disposed) this.SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        foreach (var action in this.actions) action.IsEnabled = !busy && this.administration.IsSupported;
        this.rows.IsEnabled = !busy;
        this.save.IsEnabled = this.reveal.IsEnabled = this.delete.IsEnabled = !busy;
    }

    public async ValueTask DisposeAsync()
    {
        if (this.disposed) return;
        this.disposed = true;
        await this.lifetime.CancelAsync();
        this.CloseEditor();
        if (this.active is { } operation) await operation;
        this.Items = [];
        this.rows.Children.Clear();
        this.lifetime.Dispose();
    }

    private enum EditorMode { View, Edit, Add, Delete }
}

internal sealed class SensitiveCredentialTextBox : TextBox
{
    protected override AutomationPeer OnCreateAutomationPeer() => new SensitivePeer(this);

    private sealed class SensitivePeer(SensitiveCredentialTextBox owner) : ControlAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Edit;
        protected override string GetLocalizedControlTypeCore() => "password";
        protected override string GetNameCore() => "Saved credential value";
        protected override IReadOnlyList<AutomationPeer> GetChildrenCore() => [];
    }
}

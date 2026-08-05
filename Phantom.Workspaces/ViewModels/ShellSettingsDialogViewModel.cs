using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// View model for the shell details dialog. Edits <see cref="ShellEntityOpenSpec"/> fields
/// (command line, working directory, arguments, environment variables) and, on Save, writes them
/// back to the source shell entity via the supplied entity-writer delegate.
/// </summary>
public sealed class ShellSettingsDialogViewModel : ViewModelBase
{
    private readonly EntityId? sourceEntityId;
    private readonly Func<UpdateRequest, CancellationToken, Task<UpdateResult>>? entityWriter;
    private readonly ShellEntityOpenSpec originalSpec;
    private JsonElement? sourceEntityData;
    private ConcurrencyTag? concurrencyTag;
    private string commandLine = string.Empty;
    private string workingDirectory = string.Empty;
    private string arguments = string.Empty;

    public ShellSettingsDialogViewModel(
        ShellEntityOpenSpec spec,
        EntityId? sourceEntityId,
        ConcurrencyTag? concurrencyTag,
        JsonElement? sourceEntityData,
        Func<UpdateRequest, CancellationToken, Task<UpdateResult>>? entityWriter)
    {
        ArgumentNullException.ThrowIfNull(spec);

        this.originalSpec = spec;
        this.sourceEntityId = sourceEntityId;
        this.concurrencyTag = concurrencyTag;
        this.sourceEntityData = sourceEntityData;
        this.entityWriter = entityWriter;

        this.commandLine = spec.Command;
        this.workingDirectory = spec.WorkingDirectory ?? string.Empty;
        this.arguments = string.Join(" ", spec.CommandArguments);

        this.EnvironmentVariables = new ObservableCollection<ShellEnvVarRowViewModel>();
        if (spec.Environment is not null)
        {
            foreach (var kvp in spec.Environment)
            {
                this.EnvironmentVariables.Add(new ShellEnvVarRowViewModel(this.RemoveEnvVarRow)
                {
                    Name = kvp.Key,
                    Value = kvp.Value,
                });
            }
        }

        this.AddEnvVarCommand = new RelayCommand(_ => this.AddEnvVarRow());
        this.RemoveEnvVarCommand = new RelayCommand(
            _ => this.RemoveLastEnvVarRow(),
            _ => this.EnvironmentVariables.Count > 0);
        this.SaveCommand = new AsyncRelayCommand(
            _ => this.SaveAsync(),
            _ => this.entityWriter is not null && this.sourceEntityId is not null);
    }

    public string CommandLine
    {
        get => this.commandLine;
        set => this.SetProperty(ref this.commandLine, value);
    }

    public string WorkingDirectory
    {
        get => this.workingDirectory;
        set => this.SetProperty(ref this.workingDirectory, value);
    }

    public string Arguments
    {
        get => this.arguments;
        set => this.SetProperty(ref this.arguments, value);
    }

    public ObservableCollection<ShellEnvVarRowViewModel> EnvironmentVariables { get; }

    public RelayCommand AddEnvVarCommand { get; }
    public RelayCommand RemoveEnvVarCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }

    private void AddEnvVarRow()
    {
        this.EnvironmentVariables.Add(new ShellEnvVarRowViewModel(this.RemoveEnvVarRow));
        this.RemoveEnvVarCommand.RaiseCanExecuteChanged();
    }

    private void RemoveLastEnvVarRow()
    {
        if (this.EnvironmentVariables.Count > 0)
        {
            this.EnvironmentVariables.RemoveAt(this.EnvironmentVariables.Count - 1);
            this.RemoveEnvVarCommand.RaiseCanExecuteChanged();
        }
    }

    private void RemoveEnvVarRow(ShellEnvVarRowViewModel row)
    {
        this.EnvironmentVariables.Remove(row);
        this.RemoveEnvVarCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Builds the updated spec from the current dialog fields.
    /// </summary>
    public ShellEntityOpenSpec BuildUpdatedSpec()
    {
        var args = SplitArguments(this.arguments);
        IReadOnlyDictionary<string, string>? env = null;
        if (this.EnvironmentVariables.Count > 0)
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var row in this.EnvironmentVariables)
            {
                if (!string.IsNullOrEmpty(row.Name))
                {
                    dict[row.Name] = row.Value;
                }
            }

            if (dict.Count > 0)
            {
                env = dict;
            }
        }

        return this.originalSpec with
        {
            Command = this.commandLine,
            CommandArguments = args,
            WorkingDirectory = string.IsNullOrWhiteSpace(this.workingDirectory)
                ? null
                : this.workingDirectory,
            Environment = env,
        };
    }

    /// <summary>Persists dialog fields back to the source shell entity, returning the updated spec.</summary>
    public async Task<ShellEntityOpenSpec?> SaveAsync()
    {
        var updatedSpec = this.BuildUpdatedSpec();

        if (this.entityWriter is null || this.sourceEntityId is null)
        {
            return updatedSpec;
        }

        var payload = ShellTabViewModel.BuildShellPayload(this.sourceEntityData, updatedSpec);
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

        return updatedSpec;
    }

    private static IReadOnlyList<string> SplitArguments(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return Array.Empty<string>();
        }

        return input
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }
}

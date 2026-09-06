using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentSchema;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Core.Manifest;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Reusable component that owns the parameter rows for a single, swappable manifest (issue #1463).
/// Extracted from <see cref="AgentManifestLaunchpadViewModel"/> so multiple hosts (the launchpad and
/// the workspace "New Agent" tab in #1461) can share the same parameter display, validation, and
/// value-collection logic. Persists entered values by parameter name across manifest selections;
/// values for names absent from a newly-selected manifest are retained (not shown) and only pruned
/// when a manifest is actually launched via <see cref="CommitLaunch"/>.
/// </summary>
public sealed class ManifestParametersViewModel : ViewModelBase
{
    private readonly MainWindowViewModel mainWindowViewModel;
    private readonly IReadOnlyDictionary<string, string>? initialParameterValues;
    private readonly Dictionary<string, string> retainedValues = new(StringComparer.Ordinal);
    private bool isValid = true;

    public ManifestParametersViewModel(
        MainWindowViewModel mainWindowViewModel,
        IReadOnlyDictionary<string, string>? initialParameterValues = null)
    {
        this.mainWindowViewModel = mainWindowViewModel;
        this.initialParameterValues = initialParameterValues;
    }

    public ObservableCollection<AgentManifestParameterRowViewModel> Parameters { get; } = [];

    /// <summary>
    /// Aggregate validity: true when there are no parameters or every row is valid. Raises
    /// <see cref="ViewModelBase.PropertyChanged"/> so hosts can bind their own can-start state.
    /// </summary>
    public bool IsValid
    {
        get => this.isValid;
        private set => this.SetProperty(ref this.isValid, value);
    }

    /// <summary>
    /// Completes once the executor picker options loaded by the most recent <see cref="SetManifest"/>
    /// call have finished loading (issue #1440). Exposed for deterministic tests and host seams.
    /// </summary>
    public Task ExecutorOptionsLoaded { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Rebuilds <see cref="Parameters"/> for the newly-selected manifest's schema, wiring row change
    /// notifications, recomputing <see cref="IsValid"/>, and triggering executor-option loading when
    /// any row is an executor picker. Each new row's value is seeded in precedence order:
    /// retained by-name cache → initial parameter values → manifest default.
    /// </summary>
    public void SetManifest(SubscribedEntityViewModel manifestEntity)
    {
        foreach (var existing in this.Parameters)
        {
            existing.PropertyChanged -= this.OnParameterPropertyChanged;
        }

        this.Parameters.Clear();

        var manifest = TryLoadManifest(manifestEntity);
        var parameters = manifest?.Parameters?.Properties;
        if (parameters is not null && parameters.Count > 0)
        {
            foreach (var param in parameters)
            {
                var paramName = param.Name ?? string.Empty;
                var row = new AgentManifestParameterRowViewModel
                {
                    Name = paramName,
                    DisplayName = paramName,
                    Description = param.Description ?? string.Empty,
                    IsRequired = param.Required == true,
                    ParameterKind = DetermineParameterKind(param.Kind, paramName),
                };

                var seed = SeedValueFor(paramName, param.Default);
                if (seed is not null)
                {
                    row.Value = seed;
                }

                if (!row.IsExecutorPicker)
                {
                    this.retainedValues[paramName] = row.Value;
                }

                row.PropertyChanged += this.OnParameterPropertyChanged;
                this.Parameters.Add(row);
            }
        }

        this.UpdateIsValid();

        this.ExecutorOptionsLoaded = this.Parameters.Any(p => p.IsExecutorPicker)
            ? this.LoadExecutorOptionsAsync(this.Lifetime.Token)
            : Task.CompletedTask;
    }

    /// <summary>
    /// The by-name map of non-empty text parameter values for launch. Executor-picker rows are
    /// excluded (their disambiguated choices are surfaced via <see cref="GetSelections"/>).
    /// </summary>
    public IReadOnlyDictionary<string, string> GetValues()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in this.Parameters)
        {
            if (row.IsExecutorPicker)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(row.Value))
            {
                values[row.Name] = row.Value;
            }
        }

        return values;
    }

    /// <summary>
    /// The by-name map of disambiguated executor-picker selections (issue #1440).
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement> GetSelections()
    {
        var selections = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var row in this.Parameters)
        {
            if (row.IsExecutorPicker && row.Selection is { } selection)
            {
                selections[row.Name] = selection;
            }
        }

        return selections;
    }

    /// <summary>
    /// Explicit launch commit point: prunes the retained by-name value cache down to the currently
    /// displayed manifest's parameter names. Absent names are retained (so switching manifests and
    /// back restores them) until a manifest is actually launched — this is the only place they are
    /// cleared (issue #1463).
    /// </summary>
    public void CommitLaunch()
    {
        var currentNames = new HashSet<string>(
            this.Parameters.Select(p => p.Name),
            StringComparer.Ordinal);

        var absentNames = this.retainedValues.Keys
            .Where(name => !currentNames.Contains(name))
            .ToList();

        foreach (var name in absentNames)
        {
            this.retainedValues.Remove(name);
        }
    }

    /// <summary>
    /// Determines the parameter row kind, honouring the manifest parameter's explicit <c>kind</c>
    /// field first (issue #1440, per-component-executor-binding) and falling back to name-based
    /// inference when no kind is declared.
    /// </summary>
    internal static AgentManifestParameterKind DetermineParameterKind(string? kind, string parameterName)
    {
        if (!string.IsNullOrWhiteSpace(kind))
        {
            if (string.Equals(kind, AgentManifestParameterKinds.Executor, StringComparison.Ordinal))
            {
                return AgentManifestParameterKind.Executor;
            }

            if (string.Equals(kind, "directory", StringComparison.Ordinal))
            {
                return AgentManifestParameterKind.Directory;
            }
        }

        return DetermineParameterKind(parameterName);
    }

    internal static AgentManifestParameterKind DetermineParameterKind(string parameterName)
    {
        return parameterName == "working-directory"
            ? AgentManifestParameterKind.Directory
            : AgentManifestParameterKind.Text;
    }

    private static AgentManifest? TryLoadManifest(SubscribedEntityViewModel manifestEntity)
    {
        if (manifestEntity.Data is not JsonElement data
            || !data.TryGetProperty("manifest", out var manifestElement))
        {
            return null;
        }

        try
        {
            return AgentManifestLoader.LoadManifestFromJson(manifestElement.GetRawText());
        }
        catch
        {
            return null;
        }
    }

    private string? SeedValueFor(string paramName, object? defaultValue)
    {
        if (this.retainedValues.TryGetValue(paramName, out var retained))
        {
            return retained;
        }

        if (this.initialParameterValues is not null
            && this.initialParameterValues.TryGetValue(paramName, out var initialValue))
        {
            return initialValue;
        }

        if (defaultValue is string defaultStr)
        {
            return defaultStr;
        }

        if (defaultValue is not null)
        {
            return defaultValue.ToString() ?? string.Empty;
        }

        return null;
    }

    private void OnParameterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AgentManifestParameterRowViewModel.Value)
            && sender is AgentManifestParameterRowViewModel row
            && !row.IsExecutorPicker)
        {
            this.retainedValues[row.Name] = row.Value;
        }

        if (e.PropertyName is nameof(AgentManifestParameterRowViewModel.Value)
            or nameof(AgentManifestParameterRowViewModel.IsValid))
        {
            this.UpdateIsValid();
        }
    }

    private void UpdateIsValid()
    {
        this.IsValid = this.Parameters.Count == 0
            || this.Parameters.All(p => p.IsValid);
    }

    /// <summary>
    /// Loads the combined <c>executor</c> picker options (trust-profile and user-computer-profile
    /// entities) for every executor-picker row (issue #1440). Moved from the launchpad.
    /// </summary>
    public async Task LoadExecutorOptionsAsync(CancellationToken ct = default)
    {
        var executorRows = this.Parameters.Where(p => p.IsExecutorPicker).ToList();
        if (executorRows.Count == 0)
        {
            return;
        }

        try
        {
            var dataAccessLayer = this.mainWindowViewModel.EntityBroker.EntityRepository.DataAccessLayer;

            var queryRequest = new QueryRequest
            {
                Clauses =
                [
                    new TopLevelQueryClause
                    {
                        ClauseIdentifier = new QueryClauseIdentifier { Value = "trust-profiles" },
                        Clause = new EntityTypeQueryClause
                        {
                            EntityTypeNames = new EntityTypeNameSet { Values = ["llm-trust-profile"] },
                        },
                    },
                    new TopLevelQueryClause
                    {
                        ClauseIdentifier = new QueryClauseIdentifier { Value = "user-computer-profiles" },
                        Clause = new EntityTypeQueryClause
                        {
                            EntityTypeNames = new EntityTypeNameSet { Values = ["user-computer-profile"] },
                        },
                    },
                ],
            };

            var queryResult = await dataAccessLayer.QueryAsync(queryRequest);
            var snapshotIds = queryResult.Batches
                .SelectMany(batch => batch.Entities)
                .Select(snapshot => snapshot.EntityId)
                .Distinct()
                .ToArray();

            var entities = await this.mainWindowViewModel.EntityBroker.GetEntitiesAsync(snapshotIds);

            var options = new List<ExecutorOptionViewModel>();
            foreach (var entity in entities)
            {
                if (entity.IsEntityType("user-computer-profile"))
                {
                    options.Add(new ExecutorOptionViewModel
                    {
                        Kind = ExecutorParameterSelection.UserComputerProfileKind,
                        DisplayName = $"{entity.DisplayName} (computer)",
                        Selection = ExecutorParameterSelection.ForUserComputerProfile(entity.EntityId.ToString()),
                    });
                }
                else if (entity.IsEntityType("llm-trust-profile"))
                {
                    options.Add(new ExecutorOptionViewModel
                    {
                        Kind = ExecutorParameterSelection.TrustProfileKind,
                        DisplayName = $"{entity.DisplayName} (trust policy)",
                        Selection = ExecutorParameterSelection.ForTrustProfile(GetTrustProfileNameOrId(entity)),
                    });
                }
            }

            foreach (var row in executorRows)
            {
                foreach (var option in options)
                {
                    row.ExecutorOptions.Add(option);
                }
            }
        }
        catch (Exception)
        {
            // Best-effort population; leave the picker empty on failure (required rows stay invalid).
        }
    }

    private static string GetTrustProfileNameOrId(SubscribedEntityViewModel entity)
    {
        if (entity.Data is JsonElement data
            && data.TryGetProperty("names", out var names)
            && names.ValueKind == JsonValueKind.Array
            && names.GetArrayLength() > 0)
        {
            var first = names[0];
            if (first.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(first.GetString()))
            {
                return first.GetString()!;
            }

            if (first.ValueKind == JsonValueKind.Array)
            {
                var parts = first.EnumerateArray()
                    .Where(static item => item.ValueKind == JsonValueKind.String)
                    .Select(static item => item.GetString())
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .ToArray();
                if (parts.Length > 0)
                {
                    return parts[^1]!;
                }
            }
        }

        return entity.EntityId.ToString();
    }
}

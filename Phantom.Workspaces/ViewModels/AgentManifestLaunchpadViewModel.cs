using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentSchema;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Secrets;
using Phantom.Workspaces.Services;

namespace Phantom.Workspaces.ViewModels;

public sealed class AgentManifestLaunchpadViewModel : WorkspaceTabViewModel
{
    private readonly AgentSessionShortcutContext agentSessionShortcutContext;
    private readonly OpenAgentSessionShortcutHandler openAgentSessionShortcutHandler;
    private readonly MainWindowViewModel mainWindowViewModel;
    private readonly Task executorOptionsLoadTask;
    private bool canStart;

    public SubscribedEntityViewModel ManifestEntity { get; }

    /// <summary>
    /// The hosted, reusable manifest-parameter component (issue #1463). Owns the parameter rows,
    /// their validation aggregate, value collection, and persist-by-name behavior.
    /// </summary>
    public ManifestParametersViewModel ManifestParameters { get; }

    /// <summary>Passthrough to the hosted component's parameter rows for view binding and tests.</summary>
    public ObservableCollection<AgentManifestParameterRowViewModel> Parameters => this.ManifestParameters.Parameters;

    public bool CanStart
    {
        get => this.canStart;
        private set => this.SetProperty(ref this.canStart, value);
    }

    public RelayCommand StartSessionCommand { get; }
    public RelayCommand EditManifestCommand { get; }

    public AgentManifestLaunchpadViewModel(
        SubscribedEntityViewModel manifestEntity,
        AgentSessionShortcutContext agentSessionShortcutContext,
        OpenAgentSessionShortcutHandler openAgentSessionShortcutHandler,
        MainWindowViewModel mainWindowViewModel,
        IReadOnlyDictionary<string, string>? initialParameterValues = null)
    {
        this.ManifestEntity = manifestEntity;
        this.agentSessionShortcutContext = agentSessionShortcutContext;
        this.openAgentSessionShortcutHandler = openAgentSessionShortcutHandler;
        this.mainWindowViewModel = mainWindowViewModel;

        this.ManifestParameters = new ManifestParametersViewModel(mainWindowViewModel, initialParameterValues);
        this.ManifestParameters.PropertyChanged += this.OnManifestParametersPropertyChanged;

        this.StartSessionCommand = new RelayCommand(
            async _ => await this.StartSessionAsync(),
            _ => this.CanStart);
        this.EditManifestCommand = new RelayCommand(
            async _ => await this.EditManifestAsync());

        this.ManifestParameters.SetManifest(this.ManifestEntity);
        this.UpdateCanStart();

        this.executorOptionsLoadTask = this.ManifestParameters.ExecutorOptionsLoaded;

        if (this.Parameters.Count == 0)
        {
            Lifetime.Run(this.StartSessionAsync);
        }
    }

    /// <summary>
    /// Completes once the combined <c>executor</c> picker options (trust-profile and
    /// user-computer-profile entities) have been loaded (issue #1440). Exposed for deterministic tests.
    /// </summary>
    internal Task ExecutorOptionsLoaded => this.executorOptionsLoadTask;

    private void OnManifestParametersPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ManifestParametersViewModel.IsValid))
        {
            this.UpdateCanStart();
        }
    }

    private void UpdateCanStart()
    {
        this.CanStart = this.ManifestParameters.IsValid;
        this.StartSessionCommand.RaiseCanExecuteChanged();
    }

    private async Task StartSessionAsync(CancellationToken ct = default)
    {
        if (this.ManifestEntity.Data is not JsonElement)
        {
            return;
        }

        // Collect parameter values through the hosted component, then commit the launch so retained
        // by-name values for parameters absent from this manifest are pruned (issue #1463).
        var parameterValues = this.ManifestParameters.GetValues();
        var parameterSelections = this.ManifestParameters.GetSelections();
        this.ManifestParameters.CommitLaunch();

        IReadOnlyDictionary<string, string>? parametersDict = parameterValues.Count > 0 ? parameterValues : null;
        IReadOnlyDictionary<string, JsonElement>? parameterSelectionsDict =
            parameterSelections.Count > 0 ? parameterSelections : null;

        // Launch through the shared session launcher (issue #1461) so the launchpad and the
        // workspace "New Agent" tab materialize sessions identically (create entity → open tab →
        // wire chat through IRunningAgentChatTable; the #1180 / #1109 fix lives there).
        await AgentManifestSessionLauncher.LaunchAsync(
            this.Lifetime,
            this.mainWindowViewModel,
            this.agentSessionShortcutContext,
            this.openAgentSessionShortcutHandler,
            this.ManifestEntity,
            parametersDict,
            parameterSelectionsDict);
    }

    private async Task EditManifestAsync()
    {
        await this.mainWindowViewModel.ShortcutManager.HandleShortcutAsync(
            this.mainWindowViewModel,
            Shortcut.Edit,
            this.ManifestEntity);
    }

}

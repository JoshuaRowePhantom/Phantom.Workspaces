using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AgentSchema;
using Avalonia.Threading;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.ViewModels;

public sealed class AgentManifestLaunchpadViewModel : WorkspaceTabViewModel
{
    private readonly AgentSessionShortcutContext agentSessionShortcutContext;
    private readonly OpenAgentSessionShortcutHandler openAgentSessionShortcutHandler;
    private readonly MainWindowViewModel mainWindowViewModel;
    private bool canStart;

    public SubscribedEntityViewModel ManifestEntity { get; }

    public ObservableCollection<AgentManifestParameterRowViewModel> Parameters { get; } = [];

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
        MainWindowViewModel mainWindowViewModel)
    {
        this.ManifestEntity = manifestEntity;
        this.agentSessionShortcutContext = agentSessionShortcutContext;
        this.openAgentSessionShortcutHandler = openAgentSessionShortcutHandler;
        this.mainWindowViewModel = mainWindowViewModel;

        this.StartSessionCommand = new RelayCommand(
            async _ => await this.StartSessionAsync(),
            _ => this.CanStart);
        this.EditManifestCommand = new RelayCommand(
            async _ => await this.EditManifestAsync());

        this.LoadParameters();
        this.UpdateCanStart();

        this.Parameters.CollectionChanged += this.OnParametersCollectionChanged;
    }

    private void LoadParameters()
    {
        if (this.ManifestEntity.Data is not JsonElement data
            || !data.TryGetProperty("manifest", out var manifestElement))
        {
            return;
        }

        AgentManifest manifest;
        try
        {
            manifest = AgentManifestLoader.LoadManifestFromJson(manifestElement.GetRawText());
        }
        catch
        {
            return;
        }

        var parameters = manifest.Parameters?.Properties;
        if (parameters is null || parameters.Count == 0)
        {
            return;
        }

        foreach (var param in parameters)
        {
            var row = new AgentManifestParameterRowViewModel
            {
                Name = param.Name ?? string.Empty,
                DisplayName = param.Name ?? string.Empty,
                Description = param.Description ?? string.Empty,
                IsRequired = param.Required == true,
            };

            if (param.Default is string defaultStr)
            {
                row.Value = defaultStr;
            }
            else if (param.Default is not null)
            {
                row.Value = param.Default.ToString() ?? string.Empty;
            }

            row.PropertyChanged += this.OnParameterPropertyChanged;
            this.Parameters.Add(row);
        }
    }

    private void OnParameterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AgentManifestParameterRowViewModel.Value)
            or nameof(AgentManifestParameterRowViewModel.IsValid))
        {
            this.UpdateCanStart();
        }
    }

    private void OnParametersCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        this.UpdateCanStart();
    }

    private void UpdateCanStart()
    {
        this.CanStart = this.Parameters.Count == 0
            || this.Parameters.All(p => p.IsValid);
        this.StartSessionCommand.RaiseCanExecuteChanged();
    }

    private async Task StartSessionAsync()
    {
        if (this.ManifestEntity.Data is not JsonElement data)
        {
            return;
        }

        var agentSessionId = Guid.NewGuid().ToString("n");

        // Collect parameter values
        var parameterValues = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in this.Parameters)
        {
            if (!string.IsNullOrWhiteSpace(row.Value))
            {
                parameterValues[row.Name] = row.Value;
            }
        }
        IReadOnlyDictionary<string, string>? parametersDict = parameterValues.Count > 0 ? parameterValues : null;

        var createdAgentSessionEntity = await this.agentSessionShortcutContext.CreateAgentSessionEntityAsync(
            this.mainWindowViewModel,
            this.ManifestEntity,
            agentSessionId,
            parametersDict);

        if (createdAgentSessionEntity is null)
        {
            return;
        }

        var loadingTab = new AgentSessionWorkspaceTabViewModel
        {
            Id = createdAgentSessionEntity.EntityId.ToString(),
            Title = createdAgentSessionEntity.DisplayName,
            DockRegion = "full",
            Entity = createdAgentSessionEntity,
            TabHeader = new IconTabHeaderViewModel { Icon = "🧠", Title = createdAgentSessionEntity.DisplayName },
            NotificationService = this.mainWindowViewModel.NotificationService,
        };
        await this.mainWindowViewModel.OpenTabAsync(loadingTab);

        var foregroundScheduler = TaskScheduler.FromCurrentSynchronizationContext();

        if (data.TryGetProperty("manifest", out var manifestElement))
        {
            var manifestJson = manifestElement.GetRawText();
            _ = Task.Run(async () =>
            {
                try
                {
                    var loggerFactory = new ObservableLoggerFactory();
                    var agentServices = await this.agentSessionShortcutContext
                        .CreateAgentServicesAsync(this.mainWindowViewModel, loggerFactory);
                    var agentManifest = AgentManifestLoader.LoadManifestFromJson(manifestJson);
                    var agentChat = await AgentFactory.CreateAgentChatAsync(
                        new CreateAgentChatRequest
                        {
                            AgentManifest = agentManifest,
                            Parameters = parametersDict,
                            ToolResourceFactory = agentServices.ToolResourceFactory,
                            AgentSessionId = agentSessionId,
                            AgentServices = agentServices,
                            ForegroundScheduler = foregroundScheduler,
                        });
                    var agent = this.openAgentSessionShortcutHandler.BuildAgentViewModelPublic(
                        this.mainWindowViewModel, loggerFactory, agentChat, createdAgentSessionEntity.DisplayName);
                    await Dispatcher.UIThread.InvokeAsync(() => loadingTab.SetReady(agent, loggerFactory));
                }
                catch (Exception ex)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => loadingTab.SetFailed(ex.Message));
                }
            });
        }
        else if (data.TryGetProperty("definition", out var definitionElement))
        {
            var definitionJson = definitionElement.GetRawText();
            _ = Task.Run(async () =>
            {
                try
                {
                    var loggerFactory = new ObservableLoggerFactory();
                    var agentServices = await this.agentSessionShortcutContext
                        .CreateAgentServicesAsync(this.mainWindowViewModel, loggerFactory);
                    var agentDefinition = AgentDefinition.FromJson(definitionJson);
                    var agentChat = await AgentFactory.CreateAgentChatAsync(
                        new CreateAgentChatRequest
                        {
                            AgentDefinition = agentDefinition,
                            AgentSessionId = agentSessionId,
                            AgentServices = agentServices,
                            ForegroundScheduler = foregroundScheduler,
                        });
                    var agent = this.openAgentSessionShortcutHandler.BuildAgentViewModelPublic(
                        this.mainWindowViewModel, loggerFactory, agentChat, createdAgentSessionEntity.DisplayName);
                    await Dispatcher.UIThread.InvokeAsync(() => loadingTab.SetReady(agent, loggerFactory));
                }
                catch (Exception ex)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => loadingTab.SetFailed(ex.Message));
                }
            });
        }
    }

    private async Task EditManifestAsync()
    {
        await this.mainWindowViewModel.ShortcutManager.HandleShortcutAsync(
            this.mainWindowViewModel,
            Shortcut.Edit,
            this.ManifestEntity);
    }
}

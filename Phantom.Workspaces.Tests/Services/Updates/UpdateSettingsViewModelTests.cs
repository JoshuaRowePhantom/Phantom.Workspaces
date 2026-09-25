using System;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Services.Updates;
using Phantom.Workspaces.ViewModels.Configuration;

namespace Phantom.Workspaces.Tests.Updates;

public sealed class UpdateSettingsViewModelTests
{
    [Fact]
    public void Constructor_SeedsStateFromControllerAndSettings()
    {
        var controller = new FakeUpdateController { RunningVersion = "1.0.0", RunAtStartup = true };
        var viewModel = new UpdateSettingsViewModel(
            controller,
            new UpdateSettings { Mode = AutomaticUpdateMode.DownloadAndInstall, RunAtStartup = true });

        Assert.Equal("1.0.0", viewModel.RunningVersion);
        Assert.True(viewModel.RunAtStartup);
        Assert.Equal(AutomaticUpdateMode.DownloadAndInstall, viewModel.SelectedMode.Mode);
        Assert.False(viewModel.IsUpdateAvailable);
    }

    [Fact]
    public void UpdateSettingsViewModel_UnavailableLayout_DisablesUpdateAndStartupActions()
    {
        var settings = new UpdateSettings { Mode = AutomaticUpdateMode.NotifyOnly, RunAtStartup = true };
        using var viewModel = new UpdateSettingsViewModel(null, settings, unavailableReason: "Installed update layout is broken; reinstall.");

        Assert.Contains("reinstall", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Equal("Unavailable", viewModel.RunningVersion);
        Assert.False(viewModel.CheckForUpdatesNowCommand.CanExecute(null));
        Assert.False(viewModel.InstallUpdateNowCommand.CanExecute(null));
        var unavailableStatus = viewModel.StatusText;
        viewModel.CheckForUpdatesNowCommand.Execute(null);
        viewModel.InstallUpdateNowCommand.Execute(null);
        Assert.Equal(unavailableStatus, viewModel.StatusText);
        Assert.False(viewModel.IsRunAtStartupAvailable);
        viewModel.RunAtStartup = false;
        Assert.True(viewModel.RunAtStartup);
        viewModel.SelectedMode = viewModel.Modes.Single(mode => mode.Mode == AutomaticUpdateMode.Off);
        Assert.Equal(AutomaticUpdateMode.Off, viewModel.ToSettings(settings).Mode);
        Assert.True(viewModel.ToSettings(settings).RunAtStartup);
        Assert.All(viewModel.Modes, mode => Assert.False(string.IsNullOrWhiteSpace(mode.Description)));
        viewModel.ApplyToController();
    }

    [Fact]
    public async Task UpdateSettingsViewModel_InstalledLayout_KeepsLiveCommandsAndStartup()
    {
        var controller = new FakeUpdateController { NextAvailability = new UpdateAvailability(true, "1.2.0") };
        using var viewModel = new UpdateSettingsViewModel(controller, new UpdateSettings());

        Assert.True(viewModel.IsRunAtStartupAvailable);
        Assert.True(viewModel.CheckForUpdatesNowCommand.CanExecute(null));
        viewModel.RunAtStartup = true;
        viewModel.SelectedMode = viewModel.Modes.Single(mode => mode.Mode == AutomaticUpdateMode.DownloadAndInstall);
        viewModel.CheckForUpdatesNowCommand.Execute(null);
        await controller.WaitForCheckAsync();

        Assert.True(controller.RunAtStartup);
        Assert.Equal(AutomaticUpdateMode.DownloadAndInstall, controller.Mode);
        Assert.True(viewModel.InstallUpdateNowCommand.CanExecute(null));
        viewModel.InstallUpdateNowCommand.Execute(null);
        Assert.Equal(1, controller.InstallCalls);
    }

    [Fact]
    public void ChangingMode_FlowsToControllerAndToSettings()
    {
        var controller = new FakeUpdateController();
        var viewModel = new UpdateSettingsViewModel(controller, new UpdateSettings { Mode = AutomaticUpdateMode.NotifyOnly });

        viewModel.SelectedMode = viewModel.Modes.First(option => option.Mode == AutomaticUpdateMode.Off);

        Assert.Equal(AutomaticUpdateMode.Off, controller.Mode);
        Assert.Equal(AutomaticUpdateMode.Off, viewModel.ToSettings(new UpdateSettings()).Mode);
    }

    [Fact]
    public void TogglingRunAtStartup_DelegatesToController()
    {
        var controller = new FakeUpdateController();
        var viewModel = new UpdateSettingsViewModel(controller, new UpdateSettings());

        viewModel.RunAtStartup = true;

        Assert.True(controller.RunAtStartup);
        Assert.True(viewModel.ToSettings(new UpdateSettings()).RunAtStartup);
    }

    [Fact]
    public async Task CheckForUpdatesNowCommand_UpdatesAvailabilityState()
    {
        var controller = new FakeUpdateController { NextAvailability = new UpdateAvailability(true, "1.2.0") };
        var viewModel = new UpdateSettingsViewModel(controller, new UpdateSettings());

        viewModel.CheckForUpdatesNowCommand.Execute(null);
        await controller.WaitForCheckAsync();

        Assert.True(viewModel.IsUpdateAvailable);
        Assert.Equal("1.2.0", viewModel.LatestVersion);
        Assert.Contains("1.2.0", viewModel.StatusText);
    }

    [Fact]
    public void AvailabilityChangedEvent_MarshalsThroughDispatchAndUpdatesState()
    {
        var controller = new FakeUpdateController();
        var viewModel = new UpdateSettingsViewModel(controller, new UpdateSettings(), action => action());

        controller.RaiseAvailability(new UpdateAvailability(true, "3.0.0"));

        Assert.True(viewModel.IsUpdateAvailable);
        Assert.Equal("3.0.0", viewModel.LatestVersion);
    }

    [Fact]
    public void Constructor_SeedsRunAtStartupFromPersistedSettings_NotFromController()
    {
        // Defect 2 regression: on reopen, the checkbox must reflect what was saved, not the live
        // scheduled-task state. Live and persisted disagree here to prove the ctor reads settings.
        var controller = new FakeUpdateController { RunAtStartup = false };
        var viewModel = new UpdateSettingsViewModel(controller, new UpdateSettings { RunAtStartup = true });

        Assert.True(viewModel.RunAtStartup);
    }

    [Fact]
    public void TogglingRunAtStartup_WhenControllerThrows_SurfacesErrorAndRevertsCheckbox()
    {
        var controller = new FakeUpdateController
        {
            RunAtStartup = false,
            SetRunAtStartupError = new InvalidOperationException("schtasks failed"),
        };
        var viewModel = new UpdateSettingsViewModel(controller, new UpdateSettings { RunAtStartup = false });

        var propertyChanges = new List<string?>();
        viewModel.PropertyChanged += (_, e) => propertyChanges.Add(e.PropertyName);

        viewModel.RunAtStartup = true;

        Assert.False(viewModel.RunAtStartup);
        Assert.Contains("schtasks failed", viewModel.StatusText);
        // Two PropertyChanged events for RunAtStartup: the initial set, and the revert.
        Assert.True(propertyChanges.Count(p => p == nameof(viewModel.RunAtStartup)) >= 2);
    }

    [Fact]
    public async Task SaveViaApplyToController_InvokesSetRunAtStartupWithPersistedValue()
    {
        var controller = new FakeUpdateController { RunAtStartup = false };
        var viewModel = new UpdateSettingsViewModel(controller, new UpdateSettings { RunAtStartup = true });
        // Simulate save-time reconciliation.
        viewModel.ApplyToController();
        await Task.Yield();

        Assert.True(controller.RunAtStartup);
    }

    [Fact]
    public void ApplyToController_WhenSetRunAtStartupThrows_SurfacesErrorAndDoesNotThrow()
    {
        // #1349: save-time reconciliation must not become an unhandled dispatcher crash when a
        // non-elevated environment fails to register run-at-startup. Surface via StatusText instead.
        var controller = new FakeUpdateController
        {
            RunAtStartup = false,
            SetRunAtStartupError = new UnauthorizedAccessException("Access is denied."),
        };
        var viewModel = new UpdateSettingsViewModel(controller, new UpdateSettings { RunAtStartup = true });

        var exception = Record.Exception(() => viewModel.ApplyToController());

        Assert.Null(exception);
        Assert.Contains("Access is denied.", viewModel.StatusText);
    }

    private sealed class FakeUpdateController : IUpdateController
    {
        private readonly TaskCompletionSource checkCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string RunningVersion { get; set; } = "1.0.0";

        public AutomaticUpdateMode Mode { get; set; } = AutomaticUpdateMode.NotifyOnly;

        public string? LatestAvailableVersion { get; private set; }

        public bool RunAtStartup { get; set; }

        public int InstallCalls { get; private set; }

        public bool IsRunAtStartupEnabled => this.RunAtStartup;

        public Exception? SetRunAtStartupError { get; set; }

        public UpdateAvailability NextAvailability { get; set; } = UpdateAvailability.None;

        public event EventHandler<UpdateAvailability>? UpdateAvailabilityChanged;

        public Task<UpdateAvailability> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
        {
            this.LatestAvailableVersion = this.NextAvailability.LatestVersion;
            this.UpdateAvailabilityChanged?.Invoke(this, this.NextAvailability);
            this.checkCompleted.TrySetResult();
            return Task.FromResult(this.NextAvailability);
        }

        public Task DownloadInstallAndRelaunchAsync(CancellationToken cancellationToken = default)
        {
            this.InstallCalls++;
            return Task.CompletedTask;
        }

        public void SetRunAtStartup(bool enabled)
        {
            if (this.SetRunAtStartupError is not null)
            {
                throw this.SetRunAtStartupError;
            }

            this.RunAtStartup = enabled;
        }

        public void RaiseAvailability(UpdateAvailability availability)
        {
            this.LatestAvailableVersion = availability.LatestVersion;
            this.UpdateAvailabilityChanged?.Invoke(this, availability);
        }

        public Task WaitForCheckAsync() => this.checkCompleted.Task;
    }
}

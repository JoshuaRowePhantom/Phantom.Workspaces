using Phantom.Workspaces.Install;

namespace Phantom.Workspaces.Install.Tests;

public sealed class StartupTaskServiceTests
{
    private const string CurrentExecutable = @"C:\app\current\Phantom.Workspaces.exe";

    private static StartupTaskService CreateService(
        out FakeStartupRegistration registration,
        out FakeScheduledTasks scheduledTasks)
    {
        registration = new FakeStartupRegistration();
        scheduledTasks = new FakeScheduledTasks();
        return new StartupTaskService(registration, scheduledTasks, CurrentExecutable);
    }

    [Fact]
    public void IsEnabled_IsFalseBeforeEnable()
    {
        var service = CreateService(out _, out _);
        Assert.False(service.IsEnabled());
    }

    [Fact]
    public void Enable_UsesHkcuRunRegistrationTargetingCurrent()
    {
        var service = CreateService(out var registration, out _);

        service.Enable();

        Assert.True(service.IsEnabled());
        var commandLine = registration.Entries[StartupTaskService.StartupRunValueName];
        Assert.Contains(CurrentExecutable, commandLine, StringComparison.Ordinal);
        Assert.Contains(StartupTaskService.StartupArgument, commandLine, StringComparison.Ordinal);
    }

    [Fact]
    public void Enable_DoesNotCreateAScheduledTask()
    {
        var service = CreateService(out _, out var scheduledTasks);

        service.Enable();

        Assert.Empty(scheduledTasks.Registered);
        Assert.Equal(0, scheduledTasks.RegisterCallCount);
    }

    [Fact]
    public void Enable_RemovesLegacyScheduledTask()
    {
        var service = CreateService(out _, out var scheduledTasks);
        scheduledTasks.Register(new ScheduledTaskDefinition
        {
            TaskName = StartupTaskService.StartupTaskName,
            ExecutablePath = CurrentExecutable,
            Arguments = new[] { StartupTaskService.StartupArgument },
        });

        service.Enable();

        Assert.False(scheduledTasks.Exists(StartupTaskService.StartupTaskName));
        Assert.Contains(StartupTaskService.StartupTaskName, scheduledTasks.Unregistered);
    }

    [Fact]
    public void Enable_WhenLegacyTaskRemovalFails_StillRegistersRunEntry()
    {
        var service = CreateService(out var registration, out var scheduledTasks);
        scheduledTasks.Register(new ScheduledTaskDefinition
        {
            TaskName = StartupTaskService.StartupTaskName,
            ExecutablePath = CurrentExecutable,
            Arguments = new[] { StartupTaskService.StartupArgument },
        });
        scheduledTasks.UnregisterError = new UnauthorizedAccessException("Access is denied.");

        service.Enable();

        Assert.True(registration.IsEnabled(StartupTaskService.StartupRunValueName));
    }

    [Fact]
    public void Enable_IsIdempotentAndRepointsCurrent()
    {
        var service = CreateService(out var registration, out _);

        service.Enable();
        service.Enable();

        Assert.Single(registration.Entries);
        Assert.True(service.IsEnabled());
    }

    [Fact]
    public void Disable_RemovesTheRunEntry()
    {
        var service = CreateService(out _, out _);

        service.Enable();
        service.Disable();

        Assert.False(service.IsEnabled());
    }

    [Fact]
    public void Disable_IsIdempotentWhenNotRegistered()
    {
        var service = CreateService(out _, out _);
        service.Disable();
        Assert.False(service.IsEnabled());
    }

    [Fact]
#pragma warning disable CA1416 // RealScheduledTasks is Windows-only; this test is Windows-specific
    public void BuildTaskRunCommand_QuotesExecutableAndAppendsArguments()
    {
        var command = RealScheduledTasks.BuildTaskRunCommand(new ScheduledTaskDefinition
        {
            TaskName = "x",
            ExecutablePath = CurrentExecutable,
            Arguments = new[] { "--startup" },
        });

        Assert.Equal($"\"{CurrentExecutable}\" --startup", command);
    }
#pragma warning restore CA1416
}

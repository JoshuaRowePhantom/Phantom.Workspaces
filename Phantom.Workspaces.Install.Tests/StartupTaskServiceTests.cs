using Phantom.Workspaces.Install;

namespace Phantom.Workspaces.Install.Tests;

public sealed class StartupTaskServiceTests
{
    private const string CurrentExecutable = @"C:\app\current\Phantom.Workspaces.exe";

    [Fact]
    public void IsEnabled_IsFalseBeforeEnable()
    {
        var service = new StartupTaskService(new FakeScheduledTasks(), CurrentExecutable);
        Assert.False(service.IsEnabled());
    }

    [Fact]
    public void Enable_RegistersLogonTaskTargetingCurrent()
    {
        var scheduledTasks = new FakeScheduledTasks();
        var service = new StartupTaskService(scheduledTasks, CurrentExecutable);

        service.Enable();

        Assert.True(service.IsEnabled());
        var definition = scheduledTasks.Registered[StartupTaskService.StartupTaskName];
        Assert.Equal(CurrentExecutable, definition.ExecutablePath);
        Assert.Equal(new[] { StartupTaskService.StartupArgument }, definition.Arguments);
    }

    [Fact]
    public void Enable_IsIdempotentAndRepointsCurrent()
    {
        var scheduledTasks = new FakeScheduledTasks();
        var service = new StartupTaskService(scheduledTasks, CurrentExecutable);

        service.Enable();
        service.Enable();

        Assert.Equal(2, scheduledTasks.RegisterCallCount);
        Assert.Single(scheduledTasks.Registered);
        Assert.True(service.IsEnabled());
    }

    [Fact]
    public void Disable_RemovesTheTask()
    {
        var scheduledTasks = new FakeScheduledTasks();
        var service = new StartupTaskService(scheduledTasks, CurrentExecutable);

        service.Enable();
        service.Disable();

        Assert.False(service.IsEnabled());
    }

    [Fact]
    public void Disable_IsIdempotentWhenNotRegistered()
    {
        var service = new StartupTaskService(new FakeScheduledTasks(), CurrentExecutable);
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

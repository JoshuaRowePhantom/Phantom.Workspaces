namespace Phantom.Workspaces.Containers.Tests;

[Trait("Category", "SlowDocker")]
public sealed class WindowsDockerDesktopEngineSlowTests
{
    private const string LifecycleContainerName = "pw-slow-lifecycle";
    private const string RecreateContainerName = "pw-slow-recreate";
    private static readonly string LifecycleMountRoot = Path.Combine(Path.GetTempPath(), "pw-docker-slow-lifecycle");
    private static readonly string RecreateMountRoot = Path.Combine(Path.GetTempPath(), "pw-docker-slow-recreate");

    [Fact]
    public async Task UsableAsync_WithRealDocker_ReturnsTrue()
    {
        var engine = new WindowsDockerDesktopEngine();
        await EnsureDockerUsableAsync(engine);
    }

    [Fact]
    public async Task CreateStartStopDestroy_WithRealDocker_CompletesLifecycle()
    {
        var engine = new WindowsDockerDesktopEngine();
        await EnsureDockerUsableAsync(engine);

        var containerName = LifecycleContainerName;
        var mountRoot = LifecycleMountRoot;
        var dataPath = Path.Combine(LifecycleMountRoot, "data");
        var configPath = Path.Combine(LifecycleMountRoot, "config");
        Directory.CreateDirectory(dataPath);
        Directory.CreateDirectory(configPath);

        try
        {
            await TryDestroyAsync(engine, containerName);

            var definition = new ContainerDefinition
            {
                ContainerName = containerName,
                ImageName = "nginx:alpine",
                NetworkType = ContainerNetworkType.Bridge,
                EnvironmentVariables = [],
                Mounts =
                [
                    new ContainerMountDefinition
                    {
                        Source = dataPath,
                        Target = "/tmp/data",
                        ReadOnly = false,
                    },
                    new ContainerMountDefinition
                    {
                        Source = configPath,
                        Target = "/tmp/config",
                        ReadOnly = true,
                    },
                ],
            };

            await engine.CreateAsync(definition);
            await engine.StartAsync(containerName);
            await engine.StopAsync(containerName);
            await engine.DestroyAsync(containerName);
        }
        finally
        {
            await TryDestroyAsync(engine, containerName);
            TryDeleteDirectory(mountRoot);
        }
    }

    [Fact]
    public async Task CreateAsync_WhenContainerAlreadyExists_RecreatesWithRealDocker()
    {
        var engine = new WindowsDockerDesktopEngine();
        await EnsureDockerUsableAsync(engine);

        var containerName = RecreateContainerName;
        var mountRoot = RecreateMountRoot;
        Directory.CreateDirectory(mountRoot);

        try
        {
            await TryDestroyAsync(engine, containerName);

            var definition = new ContainerDefinition
            {
                ContainerName = containerName,
                ImageName = "nginx:alpine",
                NetworkType = ContainerNetworkType.Bridge,
                EnvironmentVariables = [],
                Mounts =
                [
                    new ContainerMountDefinition
                    {
                        Source = mountRoot,
                        Target = "/tmp/recreate",
                        ReadOnly = false,
                    },
                ],
            };

            await engine.CreateAsync(definition);
            await engine.CreateAsync(definition);
            await engine.DestroyAsync(containerName);
        }
        finally
        {
            await TryDestroyAsync(engine, containerName);
            TryDeleteDirectory(mountRoot);
        }
    }

    private static async Task EnsureDockerUsableAsync(
        WindowsDockerDesktopEngine engine)
    {
        var usable = await engine.UsableAsync();
        Assert.True(usable, "Docker Desktop is not available on this machine.");
    }

    private static async Task TryDestroyAsync(
        WindowsDockerDesktopEngine engine,
        string containerName)
    {
        try
        {
            await engine.DestroyAsync(containerName);
        }
        catch (InvalidOperationException)
        {
            // Best-effort cleanup for test isolation.
        }
    }

    private static void TryDeleteDirectory(
        string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup for test isolation.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup for test isolation.
        }
    }
}

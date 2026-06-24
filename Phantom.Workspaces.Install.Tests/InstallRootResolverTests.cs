using Phantom.Workspaces.Install;

namespace Phantom.Workspaces.Install.Tests;

public sealed class InstallRootResolverTests
{
    [Fact]
    public void Resolve_PrefersExplicitOverride()
    {
        var resolved = InstallRootResolver.Resolve(
            overridePath: @"X:\sandbox\app",
            environment: _ => @"X:\env\app",
            localApplicationDataProvider: () => @"X:\local");

        Assert.Equal(@"X:\sandbox\app", resolved);
    }

    [Fact]
    public void Resolve_UsesEnvironmentVariableWhenNoOverride()
    {
        var resolved = InstallRootResolver.Resolve(
            overridePath: null,
            environment: name => name == InstallRootResolver.InstallRootEnvironmentVariable ? @"X:\env\app" : null,
            localApplicationDataProvider: () => @"X:\local");

        Assert.Equal(@"X:\env\app", resolved);
    }

    [Fact]
    public void Resolve_FallsBackToLocalApplicationData()
    {
        var resolved = InstallRootResolver.Resolve(
            overridePath: null,
            environment: _ => null,
            localApplicationDataProvider: () => @"X:\local");

        Assert.Equal(
            Path.Combine(@"X:\local", InstallRootResolver.ApplicationDirectoryName, InstallRootResolver.AppFolderName),
            resolved);
    }

    [Fact]
    public void Resolve_TreatsWhitespaceOverrideAsAbsent()
    {
        var resolved = InstallRootResolver.Resolve(
            overridePath: "   ",
            environment: _ => null,
            localApplicationDataProvider: () => @"X:\local");

        Assert.Equal(
            Path.Combine(@"X:\local", InstallRootResolver.ApplicationDirectoryName, InstallRootResolver.AppFolderName),
            resolved);
    }
}

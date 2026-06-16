using System;
using System.IO;

namespace Phantom.Workspaces.Data.MongoDB.Tests;

public sealed class MongoDbConnectionBrokerDataDirectoryTests
{
    [Fact]
    public void GetDefaultContainerDataDirectory_IsUnderUserHome_AndIndicatesPurpose()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expected = Path.Combine(home, "Phantom.Workspaces", "Mongo");

        Assert.Equal(expected, MongoDbConnectionBroker.GetDefaultContainerDataDirectory());
    }

    [Fact]
    public void GetDefaultContainerDataDirectory_IsAbsolute()
    {
        Assert.True(Path.IsPathFullyQualified(MongoDbConnectionBroker.GetDefaultContainerDataDirectory()));
    }

    [Fact]
    public void ResolveContainerDataDirectory_WhenEmpty_UsesDefault()
    {
        var connectionDefinition = new MongoDbContainerConnectionDefinition
        {
            ContainerName = "container",
            DataDirectory = string.Empty,
            DatabaseName = "database",
            CollectionName = "collection",
        };

        var resolved = MongoDbConnectionBroker.ResolveContainerDataDirectory(connectionDefinition);

        Assert.Equal(MongoDbConnectionBroker.GetDefaultContainerDataDirectory(), resolved.DataDirectory);
    }

    [Fact]
    public void ResolveContainerDataDirectory_WhenWhitespace_UsesDefault()
    {
        var connectionDefinition = new MongoDbContainerConnectionDefinition
        {
            ContainerName = "container",
            DataDirectory = "   ",
            DatabaseName = "database",
            CollectionName = "collection",
        };

        var resolved = MongoDbConnectionBroker.ResolveContainerDataDirectory(connectionDefinition);

        Assert.Equal(MongoDbConnectionBroker.GetDefaultContainerDataDirectory(), resolved.DataDirectory);
    }

    [Fact]
    public void ResolveContainerDataDirectory_ExpandsLeadingTilde_ToAbsoluteHomePath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var connectionDefinition = new MongoDbContainerConnectionDefinition
        {
            ContainerName = "container",
            DataDirectory = "~/phantom.workspaces",
            DatabaseName = "database",
            CollectionName = "collection",
        };

        var resolved = MongoDbConnectionBroker.ResolveContainerDataDirectory(connectionDefinition);

        Assert.Equal(Path.Combine(home, "phantom.workspaces"), resolved.DataDirectory);
        Assert.DoesNotContain("~", resolved.DataDirectory);
        Assert.True(Path.IsPathFullyQualified(resolved.DataDirectory));
    }

    [Fact]
    public void ResolveContainerDataDirectory_TildeOnly_ResolvesToHome()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var connectionDefinition = new MongoDbContainerConnectionDefinition
        {
            ContainerName = "container",
            DataDirectory = "~",
            DatabaseName = "database",
            CollectionName = "collection",
        };

        var resolved = MongoDbConnectionBroker.ResolveContainerDataDirectory(connectionDefinition);

        Assert.Equal(home, resolved.DataDirectory);
    }

    [Fact]
    public void ResolveContainerDataDirectory_RelativePath_IsMadeAbsolute()
    {
        var connectionDefinition = new MongoDbContainerConnectionDefinition
        {
            ContainerName = "container",
            DataDirectory = Path.Combine("relative", "mongo-data"),
            DatabaseName = "database",
            CollectionName = "collection",
        };

        var resolved = MongoDbConnectionBroker.ResolveContainerDataDirectory(connectionDefinition);

        Assert.True(Path.IsPathFullyQualified(resolved.DataDirectory));
        Assert.EndsWith(Path.Combine("relative", "mongo-data"), resolved.DataDirectory);
    }

    [Fact]
    public void ResolveContainerDataDirectory_AbsolutePath_IsPreserved()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "explicit-mongo-data");
        var connectionDefinition = new MongoDbContainerConnectionDefinition
        {
            ContainerName = "container",
            DataDirectory = absolute,
            DatabaseName = "database",
            CollectionName = "collection",
        };

        var resolved = MongoDbConnectionBroker.ResolveContainerDataDirectory(connectionDefinition);

        Assert.Equal(Path.GetFullPath(absolute), resolved.DataDirectory);
    }
}

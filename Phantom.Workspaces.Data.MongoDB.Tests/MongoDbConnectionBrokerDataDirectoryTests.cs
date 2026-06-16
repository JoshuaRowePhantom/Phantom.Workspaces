using System;
using System.IO;

namespace Phantom.Workspaces.Data.MongoDB.Tests;

public sealed class MongoDbConnectionBrokerDataDirectoryTests
{
    [Fact]
    public void NormalizeContainerDataDirectory_WhenEmpty_Throws()
    {
        var connectionDefinition = new MongoDbContainerConnectionDefinition
        {
            ContainerName = "container",
            DataDirectory = string.Empty,
            DatabaseName = "database",
            CollectionName = "collection",
        };

        Assert.Throws<InvalidOperationException>(
            () => MongoDbConnectionBroker.NormalizeContainerDataDirectory(connectionDefinition));
    }

    [Fact]
    public void NormalizeContainerDataDirectory_WhenWhitespace_Throws()
    {
        var connectionDefinition = new MongoDbContainerConnectionDefinition
        {
            ContainerName = "container",
            DataDirectory = "   ",
            DatabaseName = "database",
            CollectionName = "collection",
        };

        Assert.Throws<InvalidOperationException>(
            () => MongoDbConnectionBroker.NormalizeContainerDataDirectory(connectionDefinition));
    }

    [Fact]
    public void NormalizeContainerDataDirectory_ExpandsLeadingTilde_ToAbsoluteHomePath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var connectionDefinition = new MongoDbContainerConnectionDefinition
        {
            ContainerName = "container",
            DataDirectory = "~/phantom.workspaces",
            DatabaseName = "database",
            CollectionName = "collection",
        };

        var resolved = MongoDbConnectionBroker.NormalizeContainerDataDirectory(connectionDefinition);

        Assert.Equal(Path.Combine(home, "phantom.workspaces"), resolved.DataDirectory);
        Assert.DoesNotContain("~", resolved.DataDirectory);
        Assert.True(Path.IsPathFullyQualified(resolved.DataDirectory));
    }

    [Fact]
    public void NormalizeContainerDataDirectory_TildeOnly_ResolvesToHome()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var connectionDefinition = new MongoDbContainerConnectionDefinition
        {
            ContainerName = "container",
            DataDirectory = "~",
            DatabaseName = "database",
            CollectionName = "collection",
        };

        var resolved = MongoDbConnectionBroker.NormalizeContainerDataDirectory(connectionDefinition);

        Assert.Equal(home, resolved.DataDirectory);
    }

    [Fact]
    public void NormalizeContainerDataDirectory_RelativePath_IsMadeAbsolute()
    {
        var connectionDefinition = new MongoDbContainerConnectionDefinition
        {
            ContainerName = "container",
            DataDirectory = Path.Combine("relative", "mongo-data"),
            DatabaseName = "database",
            CollectionName = "collection",
        };

        var resolved = MongoDbConnectionBroker.NormalizeContainerDataDirectory(connectionDefinition);

        Assert.True(Path.IsPathFullyQualified(resolved.DataDirectory));
        Assert.EndsWith(Path.Combine("relative", "mongo-data"), resolved.DataDirectory);
    }

    [Fact]
    public void NormalizeContainerDataDirectory_AbsolutePath_IsPreserved()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "explicit-mongo-data");
        var connectionDefinition = new MongoDbContainerConnectionDefinition
        {
            ContainerName = "container",
            DataDirectory = absolute,
            DatabaseName = "database",
            CollectionName = "collection",
        };

        var resolved = MongoDbConnectionBroker.NormalizeContainerDataDirectory(connectionDefinition);

        Assert.Equal(Path.GetFullPath(absolute), resolved.DataDirectory);
    }
}

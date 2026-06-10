using System.IO;

namespace Phantom.Workspaces.Tests;

public sealed class RepositorySourceTests
{
    [AvaloniaFact]
    public void Parse_ReturnsLocalGitSource_ForFilesystemPath()
    {
        var source = RepositorySource.Parse(["C:\\dev\\Phantom.Workspaces-Playspace"]);

        Assert.Equal(RepositorySourceType.LocalGit, source.SourceType);
        Assert.Equal(Path.GetFullPath("C:\\dev\\Phantom.Workspaces-Playspace"), source.RawValue);
    }

    [AvaloniaFact]
    public void Parse_ReturnsWebSource_ForHttpsSource()
    {
        const string webSource = "https://example.test/repository";

        var source = RepositorySource.Parse([webSource]);

        Assert.Equal(RepositorySourceType.Web, source.SourceType);
        Assert.Equal(webSource, source.RawValue);
    }

    [AvaloniaFact]
    public void Parse_ReturnsMongoDbSource_ForNamedArguments()
    {
        var source = RepositorySource.Parse(
        [
            "--data-store",
            "mongodb",
            "--mongodb-container-name",
            "phantom-mongodb",
            "--mongodb-root-collection-name",
            "playspace",
            "--mongodb-data-directory",
            ".\\mongo-data",
            "--mongodb-host-port",
            "27017",
        ]);

        Assert.Equal(RepositorySourceType.MongoDb, source.SourceType);
        Assert.Equal("phantom-mongodb", source.MongoDbContainerName);
        Assert.Equal("playspace", source.MongoDbRootCollectionName);
        Assert.Equal(Path.GetFullPath(".\\mongo-data"), source.MongoDbDataDirectory);
        Assert.Equal(27017, source.MongoDbHostPort);
    }
}

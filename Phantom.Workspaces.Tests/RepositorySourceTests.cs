using System.IO;

namespace Phantom.Workspaces.Tests;

public sealed class RepositorySourceTests
{
    [AvaloniaFact]
    public void Parse_ReturnsLocalGitSource_ForFilesystemPath()
    {
        var source = RepositorySource.Parse(["C:\\dev\\Phantom.Workspaces-Playspace"]);

        var gitSource = Assert.IsType<LocalGitRepositorySource>(source);
        Assert.Equal(Path.GetFullPath("C:\\dev\\Phantom.Workspaces-Playspace"), gitSource.Path);
    }

    [AvaloniaFact]
    public void Parse_ReturnsWebSource_ForHttpsSource()
    {
        const string webSource = "https://example.test/repository";

        var source = RepositorySource.Parse([webSource]);

        var web = Assert.IsType<WebRepositorySource>(source);
        Assert.Equal(webSource, web.Endpoint);
    }

    [AvaloniaFact]
    public void Parse_ReturnsUnknownSource_ForNoArguments()
    {
        var source = RepositorySource.Parse([]);

        Assert.IsType<UnknownRepositorySource>(source);
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

        var mongo = Assert.IsType<MongoDbRepositorySource>(source);
        Assert.Equal("phantom-mongodb", mongo.ContainerName);
        Assert.Equal("playspace", mongo.RootCollectionName);
        Assert.Equal(Path.GetFullPath(".\\mongo-data"), mongo.DataDirectory);
        Assert.Equal(27017, mongo.HostPort);
    }
}

using System.Diagnostics;

namespace Phantom.Workspaces.Data;

public static class RepositoryStartupDiagnostics
{
    public const string SourceName = "Phantom.Workspaces.RepositoryStartup";
    private static readonly ActivitySource Source = new(SourceName);

    public static Activity? Start(string stage)
        => Source.StartActivity(stage, ActivityKind.Internal);
}

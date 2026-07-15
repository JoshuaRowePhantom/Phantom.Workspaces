using System.Xml.Linq;

namespace Phantom.Workspaces.Tests;

public sealed class SolutionFileTests
{
    [Fact]
    public void SolutionFile_AllProjectReferenceTargets_AreListedInSolution()
    {
        var root = FindRepositoryRoot();
        var solutionPath = Path.Combine(root, "Phantom.Workspaces.slnx");
        var solutionProjectPaths = XDocument.Load(solutionPath)
            .Descendants("Project")
            .Select(element => NormalizeRelativePath(element.Attribute("Path")?.Value ?? string.Empty))
            .Where(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var projectPath in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
        {
            var projectDirectory = Path.GetDirectoryName(projectPath)!;
            var project = XDocument.Load(projectPath);
            foreach (var reference in project.Descendants()
                         .Where(element => element.Name.LocalName == "ProjectReference")
                         .Select(element => element.Attribute("Include")?.Value)
                         .Where(include => !string.IsNullOrWhiteSpace(include)))
            {
                var referencedPath = Path.GetRelativePath(
                    root,
                    Path.GetFullPath(Path.Combine(projectDirectory, reference!)));

                Assert.Contains(NormalizeRelativePath(referencedPath), solutionProjectPaths);
            }
        }
    }

    private static string NormalizeRelativePath(string path)
        => path.Replace('\\', '/');

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Phantom.Workspaces.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }
}

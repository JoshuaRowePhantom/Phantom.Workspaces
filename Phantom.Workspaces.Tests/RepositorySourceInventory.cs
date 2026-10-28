using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Phantom.Workspaces.Tests;

/// <summary>
/// Physical source owned by one repository and its declared submodules. Git ignore rules are
/// deliberately irrelevant: ignored-but-owned production files must still satisfy source guards.
/// </summary>
internal sealed class RepositorySourceInventory
{
    private readonly Dictionary<string, bool> testProjectDirectories = new(StringComparer.OrdinalIgnoreCase);

    public RepositorySourceInventory(string root)
    {
        this.Root = Path.GetFullPath(root);
        var files = new List<string>();
        var repositoryRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { this.Root };
        var pending = new Stack<string>();
        pending.Push(this.Root);
        while (pending.TryPop(out var directory))
        {
            if (repositoryRoots.Contains(directory))
            {
                ReadSubmoduleRoots(directory, repositoryRoots);
            }
            else if (Directory.Exists(Path.Combine(directory, ".git"))
                     || File.Exists(Path.Combine(directory, ".git")))
            {
                continue;
            }

            files.AddRange(Directory.EnumerateFiles(directory)
                .Where(file => file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    || file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)));
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(child);
                if (name.Equals(".git", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("obj", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("bin", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                pending.Push(child);
            }
        }

        this.ProjectFiles = files.Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        this.SourceFiles = files.Where(file => file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var project in this.ProjectFiles)
        {
            var directory = Path.GetDirectoryName(project)!;
            var testProject = IsTestProject(project);
            this.testProjectDirectories[directory] = testProject
                && (!this.testProjectDirectories.TryGetValue(directory, out var previous) || previous);
        }
    }

    public string Root { get; }

    public IReadOnlyList<string> ProjectFiles { get; }

    public IReadOnlyList<string> SourceFiles { get; }

    public bool IsTestSource(string file)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(file));
        while (directory is not null)
        {
            if (this.testProjectDirectories.TryGetValue(directory, out var testProject))
            {
                return testProject;
            }

            if (string.Equals(directory, this.Root, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return false;
    }

    private static bool IsTestProject(string path)
    {
        var project = XDocument.Load(path);
        var name = Path.GetFileNameWithoutExtension(path);
        return project.Descendants().Any(element =>
                (element.Name.LocalName == "IsTestProject"
                    && string.Equals(element.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase))
                || (element.Name.LocalName == "PackageReference"
                    && string.Equals(element.Attribute("Include")?.Value, "Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase)))
            || name.EndsWith(".Tests", StringComparison.Ordinal)
            || name.EndsWith(".Test", StringComparison.Ordinal)
            || name.EndsWith(".WebViewTests", StringComparison.Ordinal)
            || name.EndsWith(".IntegrationTests", StringComparison.Ordinal);
    }

    private static void ReadSubmoduleRoots(string root, HashSet<string> repositoryRoots)
    {
        var gitmodules = Path.Combine(root, ".gitmodules");
        if (!File.Exists(gitmodules))
        {
            return;
        }

        foreach (var line in File.ReadLines(gitmodules))
        {
            var match = Regex.Match(line, "^\\s*path\\s*=\\s*(.+?)\\s*$");
            if (match.Success)
            {
                var path = match.Groups[1].Value.Trim('"').Replace('/', Path.DirectorySeparatorChar);
                repositoryRoots.Add(Path.GetFullPath(Path.Combine(root, path)));
            }
        }
    }
}

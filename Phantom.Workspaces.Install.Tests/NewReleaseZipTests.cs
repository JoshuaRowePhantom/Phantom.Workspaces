using System.Diagnostics;
using System.IO.Compression;

namespace Phantom.Workspaces.Install.Tests;

/// <summary>
/// Exercises <c>packaging\zip\New-ReleaseZip.ps1</c> to guard against the packaging flatten that
/// dropped <c>runtimes\&lt;rid&gt;\native\copilot.exe</c> to the ZIP root (issue #1377, regression of
/// #1376). The script is invoked over a synthetic publish directory and the produced ZIP is
/// inspected directly, so the structure-preserving behaviour is asserted on the real shipped seam.
/// </summary>
public sealed class NewReleaseZipTests
{
    private const string Rid = "win-x64";
    private static readonly string NestedRuntimeEntry = $"runtimes/{Rid}/native/copilot.exe";

    [Fact]
    public void NewReleaseZip_PreservesRuntimesNativeSubpath()
    {
        using var sandbox = new TempDirectory();
        var publishDirectory = SeedPublishDirectory(sandbox.Path);
        var outputDirectory = Path.Combine(sandbox.Path, "packages");

        var zipPath = InvokePackaging(publishDirectory, outputDirectory);

        using var archive = ZipFile.OpenRead(zipPath);
        var entries = archive.Entries.Select(entry => entry.FullName).ToArray();

        Assert.Contains(NestedRuntimeEntry, entries);
        Assert.DoesNotContain("copilot.exe", entries);
    }

    [Fact]
    public void ReleaseZip_Excludes_Pdb_ButKeepsStructure()
    {
        using var sandbox = new TempDirectory();
        var publishDirectory = SeedPublishDirectory(sandbox.Path);
        var outputDirectory = Path.Combine(sandbox.Path, "packages");

        var zipPath = InvokePackaging(publishDirectory, outputDirectory);

        using var archive = ZipFile.OpenRead(zipPath);
        var entries = archive.Entries.Select(entry => entry.FullName).ToArray();

        Assert.DoesNotContain(entries, entry => entry.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Phantom.Workspaces.exe", entries);
        Assert.Contains(NestedRuntimeEntry, entries);
        Assert.Contains($"runtimes/{Rid}/native/LICENSE.md", entries);
    }

    private static string SeedPublishDirectory(string root)
    {
        var publishDirectory = Path.Combine(root, "publish");
        var nativeDirectory = Path.Combine(publishDirectory, "runtimes", Rid, "native");
        Directory.CreateDirectory(nativeDirectory);

        File.WriteAllText(Path.Combine(publishDirectory, "Phantom.Workspaces.exe"), "exe-bytes");
        File.WriteAllText(Path.Combine(publishDirectory, "Phantom.Workspaces.pdb"), "pdb-bytes");
        File.WriteAllText(Path.Combine(nativeDirectory, "copilot.exe"), "copilot-bytes");
        File.WriteAllText(Path.Combine(nativeDirectory, "LICENSE.md"), "GitHub Copilot CLI License");
        return publishDirectory;
    }

    private static string InvokePackaging(string publishDirectory, string outputDirectory)
    {
        var script = Path.Combine(
            FindRepositoryRoot().FullName, "packaging", "zip", "New-ReleaseZip.ps1");
        Assert.True(File.Exists(script), $"Packaging script not found: {script}");

        var startInfo = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add("-PublishDirectory");
        startInfo.ArgumentList.Add(publishDirectory);
        startInfo.ArgumentList.Add("-Version");
        startInfo.ArgumentList.Add("0.0.0");
        startInfo.ArgumentList.Add("-RuntimeIdentifier");
        startInfo.ArgumentList.Add(Rid);
        startInfo.ArgumentList.Add("-OutputDirectory");
        startInfo.ArgumentList.Add(outputDirectory);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start pwsh for New-ReleaseZip.ps1.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(
            process.ExitCode == 0,
            $"New-ReleaseZip.ps1 failed (exit {process.ExitCode}).\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");

        var zipPath = Path.Combine(outputDirectory, $"Phantom.Workspaces-0.0.0-{Rid}.zip");
        Assert.True(File.Exists(zipPath), $"Expected release zip not produced: {zipPath}");
        return zipPath;
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Phantom.Workspaces.slnx")))
            {
                return current;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test base directory.");
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            this.Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"phantom-releasezip-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(this.Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(this.Path))
                {
                    Directory.Delete(this.Path, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}

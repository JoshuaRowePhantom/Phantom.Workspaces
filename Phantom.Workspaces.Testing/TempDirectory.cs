using System;
using System.IO;
using System.Threading;

namespace Phantom.Workspaces.Testing;

/// <summary>
/// A disposable wrapper around a freshly-created temporary directory
/// that guarantees exception-safe recursive cleanup, even when the
/// tree contains read-only files (as libgit2 writes under
/// <c>.git\objects</c>).
/// </summary>
public sealed class TempDirectory : IDisposable
{
    public string Path { get; }

    public TempDirectory(string prefix = "pw-test-")
    {
        this.Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.Path);
    }

    public static TempDirectory CreateInside(
        string parentDirectory,
        string prefix = "pw-test-")
    {
        return new TempDirectory(parentDirectory, prefix);
    }

    private TempDirectory(string parentDirectory, string prefix)
    {
        this.Path = System.IO.Path.Combine(
            parentDirectory,
            prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.Path);
    }

    public void Dispose()
    {
        ForceDelete(this.Path);
    }

    /// <summary>
    /// Best-effort recursive delete of <paramref name="path"/> that
    /// clears read-only attributes first (libgit2 writes read-only
    /// files under <c>.git\objects</c>) and retries a handful of times
    /// to tolerate the rare handle-still-closing race.
    /// </summary>
    public static void ForceDelete(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        ClearReadOnlyAttributes(path);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (IOException) when (attempt < 4)
            {
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                ClearReadOnlyAttributes(path);
            }

            Thread.Sleep(50);
        }
    }

    private static void ClearReadOnlyAttributes(string path)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(
                path,
                "*",
                SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                catch (FileNotFoundException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}

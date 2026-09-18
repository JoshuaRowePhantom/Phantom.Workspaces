using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Xml.Linq;

namespace Phantom.Workspaces.Tests;

public sealed class ApplicationIconTests
{
    private const int GroupIconResourceType = 14;
    private const uint LoadLibraryAsDataFile = 0x00000002;
    private const uint LoadLibraryAsImageResource = 0x00000020;

    [Fact]
    public void PhantomWorkspacesCsproj_ApplicationIconProperty_PointsToExistingIcoFile()
    {
        var repositoryRoot = FindRepositoryRoot();
        var projectPath = Path.Combine(
            repositoryRoot.FullName,
            "Phantom.Workspaces",
            "Phantom.Workspaces.csproj");
        var project = XDocument.Load(projectPath);
        var applicationIcon = project.Descendants("ApplicationIcon").SingleOrDefault();

        Assert.NotNull(applicationIcon);
        Assert.False(string.IsNullOrWhiteSpace(applicationIcon!.Value));
        var iconPath = Path.GetFullPath(
            Path.Combine(Path.GetDirectoryName(projectPath)!, applicationIcon.Value));
        Assert.Equal(".ico", Path.GetExtension(iconPath), StringComparer.OrdinalIgnoreCase);
        Assert.True(File.Exists(iconPath), $"Application icon not found at {iconPath}.");
    }

    [Fact]
    public void BrainIco_IconDirectoryEntries_HaveNonDegenerateByteLengths()
    {
        var iconPath = GetBrainIconPath();
        var bytes = File.ReadAllBytes(iconPath);
        using var stream = new MemoryStream(bytes);
        using var reader = new BinaryReader(stream);

        Assert.Equal(0, reader.ReadUInt16());
        Assert.Equal(1, reader.ReadUInt16());
        Assert.Equal(5, reader.ReadUInt16());

        var expectedSizes = new[] { 16, 24, 32, 48, 256 };
        var previousEnd = 6 + (16 * expectedSizes.Length);
        for (var index = 0; index < expectedSizes.Length; index++)
        {
            var widthByte = reader.ReadByte();
            var heightByte = reader.ReadByte();
            _ = reader.ReadByte();
            _ = reader.ReadByte();
            Assert.Equal(1, reader.ReadUInt16());
            Assert.Equal(32, reader.ReadUInt16());
            var byteLength = reader.ReadUInt32();
            var imageOffset = reader.ReadUInt32();
            var width = widthByte == 0 ? 256 : widthByte;
            var height = heightByte == 0 ? 256 : heightByte;

            Assert.Equal(expectedSizes[index], width);
            Assert.Equal(expectedSizes[index], height);
            Assert.True(byteLength >= 100, $"Frame {width}x{height} has only {byteLength} bytes.");
            Assert.True(imageOffset >= previousEnd);
            Assert.True(imageOffset + byteLength <= bytes.Length);
            Assert.Equal(
                new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A },
                bytes.AsSpan((int)imageOffset, 8).ToArray());
            previousEnd = checked((int)(imageOffset + byteLength));
        }

        Assert.Equal(bytes.Length, previousEnd);
    }

    [Fact]
    [SupportedOSPlatform("windows6.1")]
    public void BrainIco_SystemDrawingIcon_LoadsAtEveryDeclaredSize()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var size in new[] { 16, 24, 32, 48, 256 })
        {
            using var icon = new System.Drawing.Icon(GetBrainIconPath(), size, size);
            Assert.NotNull(icon);
        }
    }

    [Fact]
    public void PhantomWorkspacesExe_BuildOutput_EmbedsApplicationIconResource()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory)
            .Parent!
            .Name;
        var executablePath = Path.Combine(
            repositoryRoot.FullName,
            "Phantom.Workspaces",
            "bin",
            configuration,
            "net10.0",
            "Phantom.Workspaces.exe");
        Assert.True(File.Exists(executablePath), $"Application executable not found at {executablePath}.");

        var module = LoadLibraryEx(
            executablePath,
            IntPtr.Zero,
            LoadLibraryAsDataFile | LoadLibraryAsImageResource);
        Assert.NotEqual(IntPtr.Zero, module);
        try
        {
            var count = 0;
            EnumResourceNameProc callback = (_, _, _) =>
            {
                count++;
                return true;
            };

            Assert.True(EnumResourceNames(
                module,
                new IntPtr(GroupIconResourceType),
                callback,
                IntPtr.Zero));
            GC.KeepAlive(callback);
            Assert.True(count > 0, "Phantom.Workspaces.exe has no RT_GROUP_ICON resource.");
        }
        finally
        {
            Assert.True(FreeLibrary(module));
        }
    }

    private static string GetBrainIconPath() =>
        Path.Combine(
            FindRepositoryRoot().FullName,
            "Phantom.Workspaces.Gui.Shared",
            "Assets",
            "brain.ico");

    private static DirectoryInfo FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Phantom.Workspaces.slnx")))
            {
                return directory;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private delegate bool EnumResourceNameProc(IntPtr module, IntPtr type, IntPtr name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(
        string fileName,
        IntPtr file,
        uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumResourceNames(
        IntPtr module,
        IntPtr type,
        EnumResourceNameProc callback,
        IntPtr parameter);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr module);
}

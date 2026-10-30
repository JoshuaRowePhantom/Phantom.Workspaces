using System.Numerics;
using System.Text.Json;
using System.Xml.Linq;

namespace Phantom.Workspaces.Install.Tests;

[Collection(MxcIntegrationCollection.Name)]
public sealed class HelperOutputCopyTests
{
    [Fact]
    public async Task CompactDirectory_PreservesIdentifierBitsIsolationAndCleanup()
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz234567";
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var bit = -1; bit < 128; bit++)
        {
            var bytes = new byte[16];
            if (bit >= 0)
                bytes[bit / 8] = (byte)(1 << (bit % 8));
            var identifier = new Guid(bytes);
            var path = MxcRepositoryTestSupport.TestDirectory.CompactPath(
                MxcRepositoryTestSupport.Root.FullName, identifier);
            Assert.True(paths.Add(path));
            var name = Path.GetFileName(path);
            Assert.Equal(26, name.Length);
            var decoded = BigInteger.Zero;
            foreach (var character in name)
            {
                Assert.Contains(character, alphabet);
                decoded = decoded * 32 + alphabet.IndexOf(character);
            }
            Assert.Equal(new BigInteger(bytes, isUnsigned: true), decoded);
        }

        var first = new MxcRepositoryTestSupport.TestDirectory(compact: true);
        await using var second = new MxcRepositoryTestSupport.TestDirectory(compact: true);
        Assert.NotEqual(first.Path, second.Path);
        Assert.True(Directory.Exists(first.Path));
        await first.DisposeAsync();
        Assert.False(Directory.Exists(first.Path));
        Assert.True(Directory.Exists(second.Path));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MxcRepositoryTestSupport.TestDirectory(compact: true, compactPathPadding: -1));
    }

    [Theory]
    [InlineData(70, "debug")]
    [InlineData(76, "debug")]
    [InlineData(80, "debug")]
    [InlineData(70, "release_win-x64")]
    [InlineData(76, "release_win-x64")]
    [InlineData(80, "release_win-x64")]
    public void WindowsPathBudget_DeepCheckoutFitsCompleteNativeSkillPath(
        int checkoutLength,
        string pivot)
    {
        var deepCheckout = Path.Combine(
            Path.GetPathRoot(MxcRepositoryTestSupport.Root.FullName)!,
            new string('d', checkoutLength - Path.GetPathRoot(MxcRepositoryTestSupport.Root.FullName)!.Length));
        var payloadPath = Path.Combine(
            "remote-split-worker", "runtimes", "win-x64", "native",
            "builtin-skills", "customize-cloud-agent", "SKILL.md");
        var oldDestination = Path.Combine(
            deepCheckout, "TestResults", "mxc-integration-" + new string('0', 32),
            "$(MSBuildProjectName)", "bin", "Debug", "net10.0", payloadPath);
        var fixture = MxcRepositoryTestSupport.TestDirectory.CompactPath(deepCheckout, Guid.Empty);
        var destination = Path.Combine(
            fixture, "bin", "Phantom.Workspaces.Transport.Tests", pivot, payloadPath);
        var fixtureDestination = Path.Combine(
            fixture, "a", "bin", "Phantom.Workspaces.Transport.Tests", pivot, payloadPath);
        Assert.True(oldDestination.Length >= 260);
        Assert.True(destination.Length < 260);
        Assert.True(fixtureDestination.Length < 260);
    }

    public static IEnumerable<object[]> Consumers()
    {
        foreach (var consumer in new[]
                 {
                     "Phantom.Workspaces.Transport.Tests",
                     "Phantom.Workspaces.Agent.Gui.Tests",
                     "Phantom.Workspaces.Data.Core.Tests",
                     "Phantom.Workspaces.Install.Tests",
                     "Phantom.Workspaces.Llm.Core.Tests",
                 })
        {
            yield return [consumer, false, 80];
            yield return [consumer, true, 80];
        }
        yield return ["Phantom.Workspaces.Transport.Tests", true, 70];
        yield return ["Phantom.Workspaces.Transport.Tests", true, 76];
    }

    [Theory]
    [MemberData(nameof(Consumers))]
    public async Task CopyHelper_UsesEvaluatedProducerAndCompleteRelativePayload(
        string consumer,
        bool redirected,
        int checkoutLength)
    {
        Assert.True(MxcRepositoryTestSupport.Root.FullName.Length <= 80,
            "The supported Windows checkout path budget is 80 characters.");
        // Padding the isolated allocation exercises the exact prefix length of a deeper
        // checkout without touching another worktree or sharing fixture payloads.
        await using var fixture = new MxcRepositoryTestSupport.TestDirectory(
            compact: true,
            compactPathPadding: Math.Max(0, checkoutLength - MxcRepositoryTestSupport.Root.FullName.Length));
        var source = XDocument.Parse(MxcRepositoryTestSupport.Read(consumer, consumer + ".csproj"));
        var reference = Assert.Single(source.Descendants("ProjectReference"),
            element => (string?)element.Attribute("CopyTestHelper") == "true");
        var helperName = Path.GetFileNameWithoutExtension((string)reference.Attribute("Include")!);
        var producer = Path.Combine(fixture.Path, "producer", helperName + ".csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(producer)!);
        File.WriteAllText(producer, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <OutputType>Exe</OutputType>
              </PropertyGroup>
            </Project>
            """);

        var consumerPath = Path.Combine(fixture.Path, consumer + ".csproj");
        reference.SetAttributeValue("Include", producer);
        var import = Assert.Single(source.Descendants("Import"),
            element => ((string?)element.Attribute("Project"))?.EndsWith(
                "TestHelperOutput.targets", StringComparison.Ordinal) == true);
        import.SetAttributeValue("Project", Path.Combine(
            MxcRepositoryTestSupport.Root.FullName, "scripts", "TestHelperOutput.targets"));
        new XDocument(new XElement("Project",
            new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XElement("PropertyGroup", new XElement("TargetFramework", "net10.0")),
            new XElement("ItemGroup", reference),
            import)).Save(consumerPath);

        var properties = redirected
            ? new[]
            {
                "-p:UseArtifactsOutput=true",
                "-p:UseArtifactsIntermediateOutput=false",
                $"-p:ArtifactsPath={Path.Combine(fixture.Path, "a")}",
                "-p:Configuration=Release",
                "-p:RuntimeIdentifier=win-x64",
            }
            : Array.Empty<string>();
        var restore = await MxcRepositoryTestSupport.InvokeAsync(
            "dotnet", ["restore", consumerPath, "--nologo", .. properties]);
        Assert.True(restore.ExitCode == 0, restore.StandardOutput + restore.StandardError);
        var producerOutput = await EvaluateAsync(producer, properties);
        var consumerOutput = await EvaluateAsync(consumerPath, properties);
        Assert.NotEqual(producerOutput, consumerOutput);
        var ordinaryOutput = await EvaluateAsync(producer, []);
        if (redirected)
            WriteFile(ordinaryOutput, "stale-only.txt", "must never be copied");

        var payload = new Dictionary<string, string>
        {
            [helperName + ".dll"] = "current assembly",
            [helperName + ".exe"] = "current apphost",
            [helperName + ".deps.json"] = "current dependencies",
            [helperName + ".runtimeconfig.json"] = "current runtime configuration",
            ["LICENSE.md"] = "current license",
            [Path.Combine("runtimes", "win-x64", "native", "builtin-skills",
                "customize-cloud-agent", "SKILL.md")] = "deterministic native skill payload",
        };
        foreach (var (relativePath, contents) in payload)
            WriteFile(producerOutput, relativePath, contents);

        var result = await MxcRepositoryTestSupport.InvokeAsync(
            "dotnet", ["msbuild", consumerPath, "-nologo", "-t:CopyTestHelperOutput", .. properties]);
        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
        var destination = Path.Combine(
            consumerOutput, (string?)reference.Attribute("TestHelperDestination") ?? "");
        var copied = Directory.GetFiles(destination, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(destination, path)).Order().ToArray();
        Assert.Equal(payload.Keys.Order(), copied);
        foreach (var relativePath in copied)
        {
            Assert.Equal(
                MxcRepositoryTestSupport.ComputeSha256(Path.Combine(producerOutput, relativePath)),
                MxcRepositoryTestSupport.ComputeSha256(Path.Combine(destination, relativePath)));
            Assert.True(Path.Combine(destination, relativePath).Length < 260);
        }
        Assert.False(Directory.Exists(Path.Combine(producerOutput, "remote-split-worker")));

        foreach (var requiredSuffix in new[] { ".dll", ".exe", ".deps.json", ".runtimeconfig.json" })
        {
            var requiredPath = Path.Combine(producerOutput, helperName + requiredSuffix);
            File.Delete(requiredPath);
            var missing = await MxcRepositoryTestSupport.InvokeAsync(
                "dotnet", ["msbuild", consumerPath, "-nologo", "-t:CopyTestHelperOutput", .. properties]);
            Assert.NotEqual(0, missing.ExitCode);
            Assert.Contains("Required test helper output is missing", missing.StandardOutput);
            WriteFile(producerOutput, helperName + requiredSuffix, payload[helperName + requiredSuffix]);
        }

        var overlapping = await MxcRepositoryTestSupport.InvokeAsync(
            "dotnet",
            ["msbuild", consumerPath, "-nologo", "-t:CopyTestHelperOutput",
                .. properties, $"-p:OutDir={producerOutput}"]);
        Assert.NotEqual(0, overlapping.ExitCode);
        Assert.Contains("Test helper destination must not be inside its producer output", overlapping.StandardOutput);

        var unconfigured = XDocument.Load(consumerPath);
        Assert.Single(unconfigured.Descendants("ProjectReference"))
            .SetAttributeValue("CopyTestHelper", "false");
        unconfigured.Save(consumerPath);
        var designTime = await MxcRepositoryTestSupport.InvokeAsync(
            "dotnet",
            ["msbuild", consumerPath, "-nologo", "-t:CopyTestHelperOutput",
                .. properties, "-p:DesignTimeBuild=true"]);
        Assert.True(designTime.ExitCode == 0, designTime.StandardOutput + designTime.StandardError);
        var missingReference = await MxcRepositoryTestSupport.InvokeAsync(
            "dotnet", ["msbuild", consumerPath, "-nologo", "-t:CopyTestHelperOutput", .. properties]);
        Assert.NotEqual(0, missingReference.ExitCode);
        Assert.Contains("Exactly one evaluated test helper output is required", missingReference.StandardOutput);
    }

    [Theory]
    [InlineData("Debug", "")]
    [InlineData("Release", "win-x64")]
    public async Task ArtifactsLayout_IsProjectSpecificAndKeepsRestoredIntermediates(
        string configuration,
        string runtimeIdentifier)
    {
        await using var fixture = new MxcRepositoryTestSupport.TestDirectory(compact: true);
        var projects = new[]
        {
            @"Phantom.Workspaces.Transport.Tests\Phantom.Workspaces.Transport.Tests.csproj",
            @"Phantom.Workspaces.Transport.TestWorker\Phantom.Workspaces.Transport.TestWorker.csproj",
            @"Phantom.Workspaces.Llm.Test.Mcp\Phantom.Workspaces.Llm.Test.Mcp.csproj",
            @"Phantom.Workspaces.Test.WindowsProcessProbe\Phantom.Workspaces.Test.WindowsProcessProbe.csproj",
            @"Phantom.Workspaces\Phantom.Workspaces.csproj",
            @"microsoft\mxc\sdk\dotnet\Microsoft.Mxc.Sdk\Microsoft.Mxc.Sdk.csproj",
        };
        var outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects)
        {
            var projectPath = Path.Combine(MxcRepositoryTestSupport.Root.FullName, project);
            var properties = new[]
            {
                $"-p:Configuration={configuration}",
                $"-p:RuntimeIdentifier={runtimeIdentifier}",
            };
            var ordinary = await QueryAsync(projectPath, properties);
            var redirected = await QueryAsync(projectPath,
            [
                .. properties,
                "-p:UseArtifactsOutput=true",
                "-p:UseArtifactsIntermediateOutput=false",
                $"-p:ArtifactsPath={fixture.Path}",
            ]);
            var output = redirected.GetProperty("TargetDir").GetString()!;
            Assert.DoesNotContain("$(", output);
            Assert.StartsWith(fixture.Path + Path.DirectorySeparatorChar, output);
            Assert.Contains(Path.GetFileNameWithoutExtension(projectPath), output);
            Assert.Contains(configuration.ToLowerInvariant(), output);
            if (runtimeIdentifier.Length > 0)
                Assert.Contains(runtimeIdentifier, output);
            Assert.True(outputs.Add(output), $"Colliding project output: {output}");
            foreach (var property in new[] { "MSBuildProjectExtensionsPath", "ProjectAssetsFile" })
                Assert.Equal(ordinary.GetProperty(property).GetString(), redirected.GetProperty(property).GetString());
            Assert.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar,
                ordinary.GetProperty("TargetDir").GetString()!);
        }
    }

    private static async Task<string> EvaluateAsync(
        string project,
        string[] properties) =>
        (await QueryAsync(project, properties)).GetProperty("TargetDir").GetString()!;

    private static async Task<JsonElement> QueryAsync(
        string project,
        string[] properties)
    {
        var result = await MxcRepositoryTestSupport.InvokeAsync(
            "dotnet",
            ["msbuild", project, "-nologo",
                "-getProperty:TargetDir,MSBuildProjectExtensionsPath,ProjectAssetsFile", .. properties]);
        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
        using var document = JsonDocument.Parse(result.StandardOutput);
        return document.RootElement.GetProperty("Properties").Clone();
    }

    private static void WriteFile(
        string directory,
        string relativePath,
        string contents)
    {
        var path = Path.Combine(directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }
}

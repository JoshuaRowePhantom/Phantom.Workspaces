using System.Text.Json;

namespace Phantom.Workspaces.Install.Tests;

public sealed partial class CopilotWrapperNestedPublishTests
{
    [Fact]
    public Task CopilotWrapperPrerequisite_NewlyWrittenManifest_IsAcceptedByCompletedCacheValidation() =>
        AssertCompletedCacheValidationAsync("valid", true);

    [Theory]
    [InlineData("missing-entry")]
    [InlineData("missing-file")]
    public Task CopilotWrapperPrerequisite_MissingRuntimeNode_RejectsIncompleteManifest(string scenario) =>
        AssertCompletedCacheValidationAsync(scenario, false);

    [Theory]
    [InlineData("corrupt-runtime")]
    [InlineData("stale-source")]
    [InlineData("corrupt-fingerprint")]
    [InlineData("changed-provenance")]
    [InlineData("extra-entry")]
    [InlineData("duplicate-entry")]
    [InlineData("traversal")]
    [InlineData("rooted-path")]
    [InlineData("malformed-json")]
    [InlineData("old-schema")]
    [InlineData("wrong-directory")]
    public Task CopilotWrapperPrerequisite_InvalidManifest_IsRejected(string scenario) =>
        AssertCompletedCacheValidationAsync(scenario, false);

    [Fact]
    public async Task CopilotWrapperPrerequisite_UnchangedFingerprint_ReusesPreparedCacheWithoutPublishingAgain()
    {
        var project = Path.Combine(MxcRepositoryTestSupport.Root.FullName,
            "Phantom.Workspaces.Install.Tests");
        var pointer = Path.Combine(project, "obj", "Debug", "net10.0",
            "copilot-wrapper-prerequisite.path");
        var cache = File.ReadAllText(pointer).Trim();
        var manifestPath = Path.Combine(cache, "prerequisite.json");
        var originalManifest = File.ReadAllText(manifestPath);
        using var manifest = JsonDocument.Parse(originalManifest);
        Assert.Equal(15, manifest.RootElement.GetProperty("Artifacts").GetArrayLength());
        var files = manifest.RootElement.GetProperty("Artifacts").EnumerateArray()
            .Select(artifact => Path.Combine(cache,
                artifact.GetProperty("RelativePath").GetString()!.Replace('/', Path.DirectorySeparatorChar)))
            .Append(manifestPath)
            .Append(Path.Combine(cache, "prerequisite.fingerprint"))
            .ToDictionary(path => path, path =>
                (Hash: MxcRepositoryTestSupport.ComputeSha256(path),
                 Written: File.GetLastWriteTimeUtc(path)));
        Assert.Contains(Path.Combine(cache, "prepared", "runtime.node"), files.Keys);

        await AssertWarmPreparationAsync();
        await AssertWarmPreparationAsync();

        Assert.Equal(cache, File.ReadAllText(pointer).Trim());
        Assert.Equal(originalManifest, File.ReadAllText(manifestPath));
        foreach (var (path, original) in files)
        {
            Assert.Equal(original.Hash, MxcRepositoryTestSupport.ComputeSha256(path));
            Assert.Equal(original.Written, File.GetLastWriteTimeUtc(path));
        }

        async Task AssertWarmPreparationAsync()
        {
            var result = await MxcRepositoryTestSupport.InvokePowerShellAsync(
                "scripts", "prepare-copilot-wrapper-test-prerequisite.ps1",
                "-RepositoryRoot", MxcRepositoryTestSupport.Root.FullName,
                "-ProjectDirectory", project,
                "-BaseIntermediateOutputPath", Path.Combine(project, "obj"),
                "-CacheRoot", Path.Combine(project, "obj", "mxcw"),
                "-PathOutputFile", pointer);
            Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
            Assert.Contains("Using verified Copilot wrapper prerequisite", result.StandardOutput,
                StringComparison.Ordinal);
            Assert.DoesNotContain("Preparing immutable", result.StandardOutput,
                StringComparison.Ordinal);
        }
    }

    private static async Task AssertCompletedCacheValidationAsync(string scenario, bool expected)
    {
        using var directory = new MxcRepositoryTestSupport.TestDirectory();
        var script = Path.Combine(directory.Path, "validate-cache.ps1");
        File.WriteAllText(script, """
            param([string] $PreparationScript, [string] $FixtureRoot, [string] $Scenario)
            $ErrorActionPreference = 'Stop'
            $ast = [Management.Automation.Language.Parser]::ParseFile(
                $PreparationScript, [ref] $null, [ref] $null)
            foreach ($name in @('Add-HashText', 'Get-Sha256', 'Get-PreparedCacheKey', 'Test-CompletedCache'))
            {
                $function = $ast.Find({
                    param($node)
                    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
                }, $true)
                if (-not $function) { throw "Production function '$name' not found." }
                Invoke-Expression $function.Extent.Text
            }
            function Get-WriterAssignment([string] $Name)
            {
                $assignment = $ast.Find({
                    param($node)
                    $node -is [Management.Automation.Language.AssignmentStatementAst] -and
                        $node.Left.Extent.Text -ceq ('$' + $Name) -and
                        ($Name -ne 'cacheKey' -or $node.Right.Extent.Text.Contains('Get-PreparedCacheKey')) -and
                        ($Name -ne 'artifacts' -or $node.Right.Extent.Text.Contains('$artifactSources.GetEnumerator()'))
                }, $true)
                if (-not $assignment) { throw "Production writer assignment '$Name' not found." }
                return $assignment
            }
            $sources = Get-WriterAssignment 'artifactSources'
            foreach ($variable in $sources.Right.FindAll({
                param($node)
                $node -is [Management.Automation.Language.VariableExpressionAst]
            }, $true))
            {
                $name = $variable.VariablePath.UserPath
                $path = Join-Path $FixtureRoot "$name.bin"
                Set-Content -LiteralPath $path -Value "fixture $name" -NoNewline
                Set-Variable -Name $name -Value $path
            }
            $cacheRoot = Join-Path $FixtureRoot 'mxcw'
            $stagingDirectory = Join-Path $cacheRoot 'staging'
            New-Item -ItemType Directory -Path (Join-Path $stagingDirectory 'prepared') -Force | Out-Null
            Invoke-Expression $sources.Extent.Text
            Invoke-Expression (Get-WriterAssignment 'artifacts').Extent.Text
            if (@($artifacts).Count -ne 15 -or 'prepared/runtime.node' -cnotin $artifacts.RelativePath)
            {
                throw 'Production writer did not produce the complete 15-artifact contract.'
            }
            $sourceFingerprint = 'a' * 64
            $copilotSdkPackageVersion = 'fixture-sdk'
            $copilotSdkPackageSha512 = 'fixture-package-hash'
            $copilotCliVersion = 'fixture-cli'
            $copilotCliPlatform = 'win32-x64'
            $copilotCliDownloadUrl = 'https://example.invalid/fixture.tgz'
            $copilotCliChecksumsUrl = 'https://example.invalid/SHA256SUMS.txt'
            $copilotCliArchiveSha256 = 'b' * 64
            $copilotCliChecksumsSha256 = 'c' * 64
            Invoke-Expression (Get-WriterAssignment 'cacheKey').Extent.Text
            $manifest = [ordered]@{
                SchemaVersion = 5
                CacheKey = $cacheKey
                SourceFingerprint = $sourceFingerprint
                CopilotSdkPackageVersion = $copilotSdkPackageVersion
                CopilotSdkPackageSha512 = $copilotSdkPackageSha512
                CopilotCliVersion = $copilotCliVersion
                CopilotCliPlatform = $copilotCliPlatform
                CopilotCliDownloadUrl = $copilotCliDownloadUrl
                CopilotCliChecksumsUrl = $copilotCliChecksumsUrl
                CopilotCliArchiveSha256 = $copilotCliArchiveSha256
                CopilotCliChecksumsSha256 = $copilotCliChecksumsSha256
                Artifacts = @($artifacts)
            }
            $cacheDirectory = Join-Path $cacheRoot $cacheKey.Substring(0, 16)
            [IO.Directory]::Move($stagingDirectory, $cacheDirectory)
            $fingerprintPath = Join-Path $cacheDirectory 'prerequisite.fingerprint'
            Set-Content -LiteralPath $fingerprintPath -Value $cacheKey -Encoding utf8NoBOM
            switch ($Scenario)
            {
                'missing-entry' { $manifest.Artifacts = @($artifacts | Where-Object RelativePath -ne 'prepared/runtime.node') }
                'missing-file' { Remove-Item -LiteralPath (Join-Path $cacheDirectory 'prepared/runtime.node') }
                'corrupt-runtime' { Add-Content -LiteralPath (Join-Path $cacheDirectory 'prepared/runtime.node') -Value 'corrupted' }
                'stale-source' { $sourceFingerprint = 'd' * 64 }
                'corrupt-fingerprint' { Set-Content -LiteralPath $fingerprintPath -Value ('d' * 64) }
                'changed-provenance' { $manifest.CopilotCliArchiveSha256 = 'd' * 64 }
                'extra-entry' { $manifest.Artifacts += [ordered]@{ RelativePath = 'prepared/extra'; Sha256 = 'd' * 64 } }
                'duplicate-entry' { $manifest.Artifacts[0] = $manifest.Artifacts[1] }
                'traversal' { $manifest.Artifacts[0].RelativePath = '../outside' }
                'rooted-path' { $manifest.Artifacts[0].RelativePath = $FixtureRoot }
                'old-schema' { $manifest.SchemaVersion = 4 }
                'wrong-directory' {
                    $renamed = Join-Path $cacheRoot ('d' * 16)
                    [IO.Directory]::Move($cacheDirectory, $renamed)
                    $cacheDirectory = $renamed
                }
            }
            $manifestPath = Join-Path $cacheDirectory 'prerequisite.json'
            $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding utf8
            if ($Scenario -eq 'malformed-json') { Set-Content -LiteralPath $manifestPath -Value '{' }
            $accepted = Test-CompletedCache $cacheDirectory $cacheRoot $sourceFingerprint
            $guard = $ast.Find({
                param($node)
                $node -is [Management.Automation.Language.IfStatementAst] -and
                    $node.Extent.Text.StartsWith('if (-not (Test-CompletedCache $cacheDirectory $CacheRoot $sourceFingerprint))')
            }, $true)
            if (-not $guard) { throw 'Production writer completed-cache guard not found.' }
            try
            {
                Invoke-Expression $guard.Extent.Text
                if (-not $accepted) { throw 'Writer accepted an invalid completed cache.' }
            }
            catch
            {
                if ($accepted -or $_.Exception.Message -ne
                    'The newly prepared Copilot wrapper prerequisite failed completed-cache validation.')
                {
                    throw
                }
            }
            $accepted
            """);
        var result = await MxcRepositoryTestSupport.InvokePowerShellAsync(
            script, MxcRepositoryTestSupport.CreateValidatorInvocationOptions(),
            "-PreparationScript", Path.Combine(MxcRepositoryTestSupport.Root.FullName,
                "scripts", "prepare-copilot-wrapper-test-prerequisite.ps1"),
            "-FixtureRoot", directory.Path, "-Scenario", scenario);
        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
        Assert.Equal(expected.ToString(), result.StandardOutput.Trim(), ignoreCase: true);
    }
}

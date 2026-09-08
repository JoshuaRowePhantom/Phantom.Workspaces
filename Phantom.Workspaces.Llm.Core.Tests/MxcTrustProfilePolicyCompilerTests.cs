using Microsoft.Mxc.Sdk;
using Phantom.Workspaces.Llm.Processes;
using Phantom.Workspaces.Llm.Trust;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class MxcTrustProfilePolicyCompilerTests
{
    private static readonly string Root = Path.GetPathRoot(Environment.CurrentDirectory)!;
    private static readonly string Work = Path.Combine(Root, "work");
    private static readonly string Other = Path.Combine(Root, "other");

    [Fact]
    public void Compile_NoFilesystemOrNetworkPolicy_ReturnsUncontained()
    {
        var result = CreateCompiler().Compile(new TrustProfile());

        Assert.False(result.RequiresContainment);
        Assert.Null(result.Policy);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Compile_EmptyNetworkCapabilities_RequiresContainerAndGrantsNoCapabilities()
    {
        var result = CreateCompiler().Compile(new TrustProfile { NetworkCapabilities = [] });

        AssertSuccessful(result);
        Assert.Empty(result.Policy!.NetworkCapabilities);
    }

    [Fact]
    public void Compile_FilesystemPolicy_MapsReadonlyAndReadwritePaths()
    {
        var result = CreateCompiler().Compile(new TrustProfile
        {
            FilesystemPaths =
            [
                new(Work, null, TrustFilesystemAccessMode.ReadOnly),
                new(Other, null, TrustFilesystemAccessMode.ReadWrite),
            ],
        });

        AssertSuccessful(result);
        Assert.Contains(Path.GetFullPath(Work), result.Policy!.ReadonlyPaths);
        Assert.Contains(Path.GetFullPath(Other), result.Policy.ReadwritePaths);
    }

    [Fact]
    public void Compile_RemappedTarget_ReturnsValidationFailure()
    {
        var result = CreateCompiler().Compile(new TrustProfile
        {
            FilesystemPaths = [new(Work, Other, TrustFilesystemAccessMode.ReadOnly)],
        });

        AssertFailure(result, "filesystem.remappingUnsupported");
    }

    [Fact]
    public void Compile_RelativePath_ReturnsValidationFailure()
    {
        var result = CreateCompiler().Compile(new TrustProfile
        {
            FilesystemPaths = [new("relative", null, TrustFilesystemAccessMode.ReadOnly)],
        });

        AssertFailure(result, "filesystem.invalidPath");
    }

    [Theory]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\?\C:\data")]
    [InlineData(@"\\.\GLOBALROOT\Device\HarddiskVolume1")]
    [InlineData(@"C:\work\*.txt")]
    [InlineData(@"C:\work\CON")]
    [InlineData(@"C:\work\trailing.")]
    public void Compile_RemoteOrDevicePath_ReturnsValidationFailure(string path)
    {
        var result = CreateCompiler().Compile(new TrustProfile
        {
            FilesystemPaths = [new(path, null, TrustFilesystemAccessMode.ReadOnly)],
        });

        AssertFailure(result, "filesystem.invalidPath");
    }

    [Fact]
    public void Compile_UnsupportedCapability_ReturnsValidationFailure()
    {
        var result = CreateCompiler().Compile(new TrustProfile
        {
            NetworkCapabilities = ["permissiveLearningMode"],
        });

        AssertFailure(result, "network.unsupportedCapability");
    }

    [Fact]
    public void Compile_CustomCapabilityAcceptedByPinnedSdk_PreservesName()
    {
        var result = CreateCompiler().Compile(new TrustProfile
        {
            NetworkCapabilities = ["customCapability"],
        });

        AssertSuccessful(result);
        Assert.Equal(["customCapability"], result.Policy!.NetworkCapabilities);
    }

    [Fact]
    public void Compile_UnsupportedHost_ReturnsActionableFailure()
    {
        var host = new FakeMxcPolicyHost
        {
            PlatformSupport = new PlatformSupport
            {
                IsSupported = false,
                Reason = "Windows ProcessContainer is unavailable.",
            },
            Backends = [],
        };

        var result = new MxcTrustProfilePolicyCompiler(host).Compile(
            new TrustProfile { NetworkCapabilities = [] });

        var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == "host.unsupported");
        Assert.Equal(TrustProfilePolicyDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("unavailable", diagnostic.Message);
        Assert.Null(result.Policy);
    }

    [Fact]
    public void Compile_DataSharingFull_NoEnvironmentOverride()
    {
        var result = CreateCompiler().Compile(new TrustProfile { NetworkCapabilities = [] });

        AssertSuccessful(result);
        Assert.DoesNotContain("COPILOT_CONFIG_HOME", result.Policy!.EnvironmentOverrides);
        Assert.Contains(Path.Combine(Root, "user", ".copilot"), result.Policy.ReadwritePaths);
    }

    [Fact]
    public void Compile_DataSharingRegime_GrantsRegimeScopedPathsAndOverride()
    {
        var result = CreateCompiler().Compile(new TrustProfile
        {
            NetworkCapabilities = [],
            DataSharing = TrustDataSharing.Regime("regulated"),
        });

        AssertSuccessful(result);
        var expected = Path.Combine(Root, "user", ".copilot", "regulated-session");
        Assert.Equal(expected, result.Policy!.EnvironmentOverrides["COPILOT_CONFIG_HOME"]);
        Assert.Contains(expected, result.Policy.ReadwritePaths);
    }

    [Fact]
    public void Compile_DataSharingNone_GrantsEphemeralPathsAndOverride()
    {
        var result = CreateCompiler().Compile(new TrustProfile
        {
            NetworkCapabilities = [],
            DataSharing = TrustDataSharing.None,
        });

        AssertSuccessful(result);
        var expected = Path.Combine(Root, "temp", "copilot-ephemeral-nonce");
        Assert.Equal(expected, result.Policy!.EnvironmentOverrides["COPILOT_CONFIG_HOME"]);
        Assert.Contains(expected, result.Policy.ReadwritePaths);
        Assert.Contains(Path.Combine(Root, "temp", "copilot-session-nonce"), result.Policy.ReadwritePaths);
    }

    [Fact]
    public void Compile_DataSharingNoneWithoutOtherRestrictions_RequiresContainment()
    {
        var result = CreateCompiler().Compile(new TrustProfile
        {
            DataSharing = TrustDataSharing.None,
        });

        AssertSuccessful(result);
        Assert.Contains("COPILOT_CONFIG_HOME", result.Policy!.EnvironmentOverrides.Keys);
    }

    [Fact]
    public void Compile_RegimeScopedSharing_CreatesEphemeralSessionTempDir()
    {
        var result = CreateCompiler().Compile(new TrustProfile
        {
            NetworkCapabilities = [],
            DataSharing = TrustDataSharing.Regime("regulated"),
        });

        AssertSuccessful(result);
        Assert.Contains(
            Path.Combine(Root, "temp", "copilot-regulated-nonce"),
            result.Policy!.ReadwritePaths);
    }

    [Fact]
    public void Compile_DataSharingEnforcesCopilotPathGrant()
    {
        var result = CreateCompiler().Compile(new TrustProfile { NetworkCapabilities = [] });

        AssertSuccessful(result);
        Assert.Contains(Path.Combine(Root, "cli"), result.Policy!.ReadonlyPaths);
        Assert.Contains(result.Diagnostics, item => item.Code == "filesystem.bootstrapGrant");
    }

    [Fact]
    public void Compile_DaclFallbackAvailable_DoesNotRejectPolicy()
    {
        var host = new FakeMxcPolicyHost
        {
            Backends =
            [
                new AvailableBackend
                {
                    Backend = ContainmentBackend.ProcessContainer,
                    Tier = IsolationTier.AppContainerDacl,
                },
            ],
        };

        var result = new MxcTrustProfilePolicyCompiler(host).Compile(
            new TrustProfile { NetworkCapabilities = [] });

        AssertSuccessful(result);
        Assert.Contains(result.Diagnostics, item => item.Code == "host.daclFallback");
    }

    [Fact]
    public void Compile_RepeatedWithSameSession_IsDeterministic()
    {
        var compiler = CreateCompiler();
        var profile = new TrustProfile
        {
            NetworkCapabilities = [],
            DataSharing = TrustDataSharing.None,
        };

        var first = compiler.Compile(profile);
        var second = compiler.Compile(profile);

        Assert.Equal(first.Policy!.ReadonlyPaths, second.Policy!.ReadonlyPaths);
        Assert.Equal(first.Policy.ReadwritePaths, second.Policy.ReadwritePaths);
        Assert.Equal(first.Policy.EnvironmentOverrides, second.Policy.EnvironmentOverrides);
        Assert.Equal(first.Diagnostics, second.Diagnostics);
    }

    private static MxcTrustProfilePolicyCompiler CreateCompiler() =>
        new(new FakeMxcPolicyHost());

    private static void AssertSuccessful(TrustProfileProcessPolicyCompilation result)
    {
        Assert.True(result.RequiresContainment);
        Assert.NotNull(result.Policy);
        Assert.DoesNotContain(
            result.Diagnostics,
            item => item.Severity == TrustProfilePolicyDiagnosticSeverity.Error);
        Assert.Equal(MxcContainmentBackend.ProcessContainer, result.Policy!.Containment.Backend);
        Assert.False(result.Policy.Containment.LearningMode);
        Assert.False(result.Policy.Containment.PermissiveMode);
    }

    private static void AssertFailure(
        TrustProfileProcessPolicyCompilation result,
        string expectedCode)
    {
        Assert.True(result.RequiresContainment);
        Assert.Null(result.Policy);
        Assert.Contains(
            result.Diagnostics,
            item => item.Code == expectedCode
                && item.Severity == TrustProfilePolicyDiagnosticSeverity.Error);
    }
}

internal sealed class FakeMxcPolicyHost : IMxcPolicyHost
{
    private static readonly string Root = Path.GetPathRoot(Environment.CurrentDirectory)!;

    public string CopilotBaseDirectory => Path.Combine(Root, "user", ".copilot");
    public string TemporaryDirectory => Path.Combine(Root, "temp");
    public string CopilotRuntimeDirectory => Path.Combine(Root, "cli");
    public string SessionNonce => "nonce";

    public PlatformSupport PlatformSupport { get; init; } = new()
    {
        IsSupported = true,
        AvailableMethods = [ContainmentBackend.ProcessContainer],
    };

    public IReadOnlyList<AvailableBackend> Backends { get; init; } =
    [
        new AvailableBackend
        {
            Backend = ContainmentBackend.ProcessContainer,
            Tier = IsolationTier.BaseContainer,
        },
    ];

    public PlatformSupport GetPlatformSupport() => PlatformSupport;
    public IReadOnlyList<AvailableBackend> GetAvailableBackends() => Backends;
}

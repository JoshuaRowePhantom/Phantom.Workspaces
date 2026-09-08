using System.Text.Json;
using Phantom.Workspaces.Llm.Processes;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class MxcProcessPolicySerializationTests
{
    [Fact]
    public void MxcProcessPolicy_SerializeDeserialize_RoundTripsExactly()
    {
        var policy = new MxcProcessPolicy(
            MxcProcessPolicy.CurrentSchemaVersion,
            [@"C:\read"],
            [@"C:\write"],
            ["internetClient"],
            new Dictionary<string, string> { ["COPILOT_CONFIG_HOME"] = @"C:\config" },
            new MxcProcessContainment(
                MxcContainmentBackend.ProcessContainer,
                LeastPrivilege: true,
                LearningMode: false,
                PermissiveMode: false));

        var json = JsonSerializer.Serialize(policy);
        var deserialized = JsonSerializer.Deserialize<MxcProcessPolicy>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(policy.SchemaVersion, deserialized.SchemaVersion);
        Assert.Equal(policy.ReadonlyPaths, deserialized.ReadonlyPaths);
        Assert.Equal(policy.ReadwritePaths, deserialized.ReadwritePaths);
        Assert.Equal(policy.NetworkCapabilities, deserialized.NetworkCapabilities);
        Assert.Equal(policy.EnvironmentOverrides, deserialized.EnvironmentOverrides);
        Assert.Equal(policy.Containment, deserialized.Containment);
    }

    [Fact]
    public void MxcProcessPolicy_ConstructorDefensivelyCopiesCollections()
    {
        var readonlyPaths = new List<string> { @"C:\read" };
        var environment = new Dictionary<string, string>
        {
            ["COPILOT_CONFIG_HOME"] = @"C:\config",
        };
        var policy = new MxcProcessPolicy(
            MxcProcessPolicy.CurrentSchemaVersion,
            readonlyPaths,
            [],
            [],
            environment,
            new MxcProcessContainment(
                MxcContainmentBackend.ProcessContainer,
                true,
                false,
                false));

        readonlyPaths.Add(@"C:\injected");
        environment["INJECTED"] = "value";

        Assert.Equal([@"C:\read"], policy.ReadonlyPaths);
        Assert.DoesNotContain("INJECTED", policy.EnvironmentOverrides);
    }
}

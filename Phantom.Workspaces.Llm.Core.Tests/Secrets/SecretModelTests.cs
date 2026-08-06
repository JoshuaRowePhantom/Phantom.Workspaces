using System;
using System.Security;
using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Llm.Secrets;

namespace Phantom.Workspaces.Llm.Core.Tests.Secrets;

public class SecretModelTests
{
    [Fact]
    public void SecretUseMemory_ValueEquality_HoldsForSameValues()
    {
        var a = new SecretUseMemory(SecretUseScope.AllUses, "display", "hash");
        var b = new SecretUseMemory(SecretUseScope.AllUses, "display", "hash");

        Assert.Equal(a, b);
    }

    [Fact]
    public void SecretUseMemory_ValueEquality_DiffersOnHash()
    {
        var a = new SecretUseMemory(SecretUseScope.AllUses, "display", "hash1");
        var b = new SecretUseMemory(SecretUseScope.AllUses, "display", "hash2");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void MemorizedSecret_ValueEquality_HoldsForSameValues()
    {
        var when = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var memory = new SecretUseMemory(SecretUseScope.AnyManifest, "display", "hash");
        var a = new MemorizedSecret(memory, new GitHubLoginSecretSource(), when);
        var b = new MemorizedSecret(memory, new GitHubLoginSecretSource(), when);

        Assert.Equal(a, b);
    }

    [Fact]
    public void SecretSource_JsonPolymorphism_RoundTripsSubtypes()
    {
        SecretSource[] sources =
        {
            new GitHubLoginSecretSource(),
            new AwsLoginSecretSource(),
            new AzureLoginSecretSource(),
            new CredentialStoreSecretSource("my-cred"),
        };

        foreach (var source in sources)
        {
            var json = JsonSerializer.Serialize(source);
            var roundTripped = JsonSerializer.Deserialize<SecretSource>(json);
            Assert.Equal(source, roundTripped);
        }
    }

    [Fact]
    public void CredentialStoreSecretSource_ExposesCredentialName()
    {
        var source = new CredentialStoreSecretSource("cred-name");

        Assert.Equal("cred-name", source.CredentialName);
    }

    [Fact]
    public void SecretRequest_HoldsCandidateSourcesAndMemories()
    {
        var memory = new SecretUseMemory(SecretUseScope.AllUses, "d", "h");
        SecretSource defaultSource = new GitHubLoginSecretSource();
        var request = new SecretRequest(
            "SECRET",
            "env.API_KEY",
            new[] { memory },
            defaultSource,
            new SecretSource[] { defaultSource, new AwsLoginSecretSource() });

        Assert.Equal("SECRET", request.SecretName);
        Assert.Single(request.Memories);
        Assert.Equal(2, request.CandidateSecretSources.Count);
    }

    [Fact]
    public void SecretRequestFailure_CarriesReason()
    {
        var failure = new SecretRequestFailure("SECRET", "does not exist", SecretRequestFailureReason.DoesntExist);

        Assert.Equal(SecretRequestFailureReason.DoesntExist, failure.Reason);
        Assert.Equal("SECRET", failure.SecretName);
    }

    [Fact]
    public void RequestSecretsResult_HoldsAcquiredAndFailed()
    {
        var retriever = new SecretRetriever
        {
            SecretName = "SECRET",
            Secret = _ => Task.FromResult(new SecureString()),
        };
        var failure = new SecretRequestFailure("OTHER", "boom", SecretRequestFailureReason.Other);
        var result = new RequestSecretsResult(new[] { retriever }, new[] { failure });

        Assert.Single(result.AcquiredSecrets);
        Assert.Single(result.FailedSecrets);
    }

    [Fact]
    public void SecretMaterializationFailedException_CarriesFailures()
    {
        var failures = new[]
        {
            new SecretRequestFailure("SECRET", "boom", SecretRequestFailureReason.ErrorReading),
        };
        var ex = new SecretMaterializationFailedException("failed", failures);

        Assert.Single(ex.Failures);
        Assert.Equal("failed", ex.Message);
    }

    [Fact]
    public void SecretMaterializationRefusedException_CarriesMessage()
    {
        var ex = new SecretMaterializationRefusedException("refused");

        Assert.Equal("refused", ex.Message);
    }
}

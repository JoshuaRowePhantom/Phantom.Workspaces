using Phantom.Workspaces.Llm.Secrets;

namespace Phantom.Workspaces.Llm.Core.Tests.Secrets;

public class SecretUseScopePreimageTests
{
    private const string Prefix = "phantom.workspaces/secret-store/v1";

    [Fact]
    public void Build_AllUses_ProducesFixedPreimageIndependentOfSecretName()
    {
        var a = SecretUseScopePreimage.Build(SecretUseScope.AllUses, "secretA", "useX");
        var b = SecretUseScopePreimage.Build(SecretUseScope.AllUses, "secretB", "useY");

        Assert.Equal($"{Prefix}|scope=all-uses", a);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Build_AnyManifest_EmbedsSecretNameNotUsePath()
    {
        var preimage = SecretUseScopePreimage.Build(SecretUseScope.AnyManifest, "MY_SECRET", "some-use");

        Assert.Equal($"{Prefix}|scope=any-manifest|secret=MY_SECRET", preimage);
        Assert.DoesNotContain("use=", preimage);
    }

    [Fact]
    public void Build_KeyInAnyManifest_EmbedsSecretNameAndUsePath()
    {
        var preimage = SecretUseScopePreimage.Build(SecretUseScope.KeyInAnyManifest, "MY_SECRET", "env.API_KEY");

        Assert.Equal($"{Prefix}|scope=key-any-manifest|secret=MY_SECRET|use=env.API_KEY", preimage);
    }

    [Fact]
    public void Build_ManifestIdentity_EmbedsIdentityAndSecretNameNotContent()
    {
        var preimage = SecretUseScopePreimage.Build(
            SecretUseScope.ManifestIdentity, "MY_SECRET", "some-use", stableManifestIdentity: "manifest-123");

        Assert.Equal($"{Prefix}|scope=manifest-identity|manifestId=manifest-123|secret=MY_SECRET", preimage);
        Assert.DoesNotContain("manifestHash=", preimage);
    }

    [Fact]
    public void Build_ManifestContent_EmbedsContentHashAndSecretName()
    {
        var preimage = SecretUseScopePreimage.Build(
            SecretUseScope.ManifestContent, "MY_SECRET", "some-use", manifestContentHash: "abc123");

        Assert.Equal($"{Prefix}|scope=manifest-content|manifestHash=abc123|secret=MY_SECRET", preimage);
    }

    [Fact]
    public void Build_KeyInManifestContent_EmbedsContentHashAndSecretNameAndUsePath()
    {
        var preimage = SecretUseScopePreimage.Build(
            SecretUseScope.KeyInManifestContent, "MY_SECRET", "env.API_KEY", manifestContentHash: "abc123");

        Assert.Equal($"{Prefix}|scope=key-manifest-content|manifestHash=abc123|secret=MY_SECRET|use=env.API_KEY", preimage);
    }

    [Theory]
    [InlineData(SecretUseScope.AllUses)]
    [InlineData(SecretUseScope.AnyManifest)]
    [InlineData(SecretUseScope.KeyInAnyManifest)]
    [InlineData(SecretUseScope.ManifestIdentity)]
    [InlineData(SecretUseScope.ManifestContent)]
    [InlineData(SecretUseScope.KeyInManifestContent)]
    public void Build_AllScopes_UseVersionPrefix(SecretUseScope scope)
    {
        var preimage = SecretUseScopePreimage.Build(
            scope, "MY_SECRET", "env.API_KEY", stableManifestIdentity: "id", manifestContentHash: "hash");

        Assert.StartsWith(Prefix + "|", preimage);
    }

    [Fact]
    public void Build_AlwaysAsk_ProducesEmptyPreimage()
    {
        var preimage = SecretUseScopePreimage.Build(SecretUseScope.AlwaysAsk, "MY_SECRET", "use");

        Assert.Equal(string.Empty, preimage);
    }

    [Fact]
    public void Build_AllUses_TwoDifferentSecrets_ProduceSameHash()
    {
        var a = SecretUseScopePreimage.ComputeHash(SecretUseScope.AllUses, "secretA", "useX");
        var b = SecretUseScopePreimage.ComputeHash(SecretUseScope.AllUses, "secretB", "useY");

        Assert.Equal(a, b);
        Assert.NotEqual(string.Empty, a);
    }

    [Fact]
    public void ComputeHash_AlwaysAsk_ReturnsEmptyAndNeverMatches()
    {
        var a = SecretUseScopePreimage.ComputeHash(SecretUseScope.AlwaysAsk, "secretA", "useX");
        var b = SecretUseScopePreimage.ComputeHash(SecretUseScope.AlwaysAsk, "secretA", "useX");

        Assert.Equal(string.Empty, a);
        Assert.Equal(string.Empty, b);
    }

    [Fact]
    public void ComputeHash_DifferentScopes_ProduceDifferentHashes()
    {
        var anyManifest = SecretUseScopePreimage.ComputeHash(SecretUseScope.AnyManifest, "MY_SECRET", "use");
        var keyInAnyManifest = SecretUseScopePreimage.ComputeHash(SecretUseScope.KeyInAnyManifest, "MY_SECRET", "use");

        Assert.NotEqual(anyManifest, keyInAnyManifest);
    }
}

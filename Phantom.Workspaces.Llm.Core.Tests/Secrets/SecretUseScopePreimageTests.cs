using System.Collections.Generic;
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

    [Fact]
    public void SecretUseScopePreimage_ExistingScopeHashes_UnchangedByAddingSessionInput()
    {
        // Golden hashes for the pre-existing scopes. Adding the sessionIdentity parameter must not
        // change any existing branch, so previously-persisted grants keep matching (#1401). The
        // VersionPrefix must not be bumped.
        var golden = new Dictionary<SecretUseScope, string>
        {
            [SecretUseScope.AllUses] = "ca6f2134a9ba19ef293c4e3136a7dcc84edcc753c22ca3950d39b2cb4d6295a4",
            [SecretUseScope.AnyManifest] = "850659b70107e1ae3ab0380a876529e6f9440f9ff4c0699a4b1fbee40ae742e7",
            [SecretUseScope.KeyInAnyManifest] = "e1d9aedffcebb5cffff690b7bdf11088540a37f31f19ea379f442c48537651c2",
            [SecretUseScope.ManifestIdentity] = "46e768b0469f66e9d2a81e6843770416cc6ac26bda65a714b354e9e57fdcb991",
            [SecretUseScope.ManifestContent] = "5814044a28ece12053b311ebac87b8e40f10054374b8e6018cf4289b4bf6e580",
            [SecretUseScope.KeyInManifestContent] = "dc1c9db9039aa687f84887270b7ad76568c42fa5eb6463e786fca852376255c1",
        };

        foreach (var (scope, expected) in golden)
        {
            var withoutSession = SecretUseScopePreimage.ComputeHash(
                scope, "MY_SECRET", "env.API_KEY", stableManifestIdentity: "id", manifestContentHash: "hash");
            var withSession = SecretUseScopePreimage.ComputeHash(
                scope, "MY_SECRET", "env.API_KEY", stableManifestIdentity: "id", manifestContentHash: "hash",
                sessionIdentity: "session-xyz");

            Assert.Equal(expected, withoutSession);
            Assert.Equal(expected, withSession);
        }
    }

    [Fact]
    public void SecretUseScopePreimage_SessionIdentityScope_DependsOnSessionIdNotManifest()
    {
        var a = SecretUseScopePreimage.Build(
            SecretUseScope.SessionIdentity, "MY_SECRET", "use",
            stableManifestIdentity: "manifest-1", manifestContentHash: "content-1", sessionIdentity: "session-A");
        var differentSession = SecretUseScopePreimage.Build(
            SecretUseScope.SessionIdentity, "MY_SECRET", "use",
            stableManifestIdentity: "manifest-1", manifestContentHash: "content-1", sessionIdentity: "session-B");
        var differentManifest = SecretUseScopePreimage.Build(
            SecretUseScope.SessionIdentity, "MY_SECRET", "use",
            stableManifestIdentity: "manifest-2", manifestContentHash: "content-2", sessionIdentity: "session-A");

        Assert.Equal($"{Prefix}|scope=session-identity|sessionId=session-A|secret=MY_SECRET", a);
        Assert.DoesNotContain("manifestId=", a);
        Assert.DoesNotContain("manifestHash=", a);
        Assert.NotEqual(a, differentSession);
        Assert.Equal(a, differentManifest);
    }

    [Fact]
    public void SecretUseScopePreimage_ManifestIdentityScope_IndependentOfSessionId()
    {
        var sessionA = SecretUseScopePreimage.ComputeHash(
            SecretUseScope.ManifestIdentity, "MY_SECRET", "use",
            stableManifestIdentity: "manifest-1", sessionIdentity: "session-A");
        var sessionB = SecretUseScopePreimage.ComputeHash(
            SecretUseScope.ManifestIdentity, "MY_SECRET", "use",
            stableManifestIdentity: "manifest-1", sessionIdentity: "session-B");

        Assert.Equal(sessionA, sessionB);
    }
}

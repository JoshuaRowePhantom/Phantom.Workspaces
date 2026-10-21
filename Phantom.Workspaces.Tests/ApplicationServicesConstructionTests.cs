using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Services;

namespace Phantom.Workspaces.Tests;

public sealed class ApplicationServicesConstructionTests
{
    [Fact]
    public void ApplicationServices_ConstructionBorrowsExactRequiredBroker()
    {
        var broker = Mock.Of<IAccessBroker>();
        var cache = Mock.Of<IAgentPersistenceStoreCache>();
        var services = new ApplicationServices(null!, cache, NullLoggerFactory.Instance, broker);

        Assert.Same(broker, services.AccessBroker);
        Assert.Same(cache, services.AgentPersistenceStoreCache);
        var parameter = Assert.Single(typeof(ApplicationServices).GetConstructors()).GetParameters()[3];
        Assert.Equal("accessBroker", parameter.Name);
        Assert.Equal(typeof(IAccessBroker), parameter.ParameterType);
        Assert.False(parameter.HasDefaultValue);
    }

    [Fact]
    public void ApplicationServices_ConstructionRejectsMissingBroker()
    {
        Assert.Throws<ArgumentNullException>(() => new ApplicationServices(
            null!, Mock.Of<IAgentPersistenceStoreCache>(), NullLoggerFactory.Instance, null!));
    }

    [Fact]
    public void ApplicationServices_DoesNotExposeOrConstructLegacySecretGraph()
    {
        var type = typeof(ApplicationServices);
        var legacyProperties = new[] { "SecretProvider", "CredentialPicker", "AllowedSecretsStore", "PlatformSecretStore" };
        Assert.All(legacyProperties, property => Assert.Null(type.GetProperty(property)));
        Assert.Null(type.GetMethod("CreateDefaultSecretServices", BindingFlags.Static | BindingFlags.NonPublic));
        var parameters = Assert.Single(type.GetConstructors()).GetParameters().Select(parameter => parameter.Name).ToArray();
        Assert.DoesNotContain("secretProvider", parameters);
        Assert.DoesNotContain("credentialPicker", parameters);
        Assert.DoesNotContain("allowedSecretsStore", parameters);
        Assert.DoesNotContain("platformSecretStore", parameters);
    }

    [Fact]
    public void ApplicationServices_StartupUpdateCapability_PropagatesUnavailableReason()
    {
        var services = new ApplicationServices(
            null!, Mock.Of<IAgentPersistenceStoreCache>(), NullLoggerFactory.Instance, Mock.Of<IAccessBroker>());

        services.SetUpdateCapability(null, "Installed update layout is broken; reinstall.");

        Assert.Null(services.UpdateController);
        Assert.Contains("reinstall", services.UpdateUnavailableReason, StringComparison.Ordinal);
    }
}

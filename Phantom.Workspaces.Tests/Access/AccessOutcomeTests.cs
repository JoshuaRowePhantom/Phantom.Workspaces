using System.Reflection;
using System.Runtime.CompilerServices;
using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.Tests.Access;

public sealed class AccessOutcomeTests
{
    [Fact]
    public void AccessOutcome_AllVariants_AreClosedTypedRecords()
    {
        var variants = typeof(AccessOutcome<>).Assembly.GetTypes()
            .Where(type => type.BaseType?.IsGenericType == true &&
                type.BaseType.GetGenericTypeDefinition() == typeof(AccessOutcome<>))
            .ToArray();

        Assert.Equal(7, variants.Length);
        Assert.All(variants, type =>
        {
            Assert.True(type.IsSealed);
            Assert.NotNull(type.GetMethod("<Clone>$"));
            Assert.Contains(typeof(IAccessResult), type.GetGenericArguments()[0].GetGenericParameterConstraints());
        });
    }

    [Fact]
    public void AccessOutcome_RequiredAction_ContainsOnlySafeRequestIdentifier()
    {
        foreach (var type in new[] { typeof(AccessAuthorizationRequired<>), typeof(AccessReacquisitionRequired<>) })
        {
            var property = Assert.Single(type.GetProperties(BindingFlags.Public | BindingFlags.Instance));
            Assert.Equal(typeof(UserAuthorizationRequestIdentifier), property.PropertyType);
            Assert.Equal(typeof(Guid), Assert.Single(property.PropertyType.GetProperties()).PropertyType);
        }
    }

    [Fact]
    public void AccessFailed_RequiresCodeSafeMessageAndRetryFlag()
    {
        var properties = typeof(AccessFailed<>).GetProperties();
        Assert.Equal(new[] { "CanRetry", "ErrorCode", "SafeMessage" }, properties.Select(property => property.Name).Order().ToArray());
        Assert.All(properties, property => Assert.NotNull(property.GetCustomAttribute<RequiredMemberAttribute>()));
        Assert.DoesNotContain(properties, property => typeof(Exception).IsAssignableFrom(property.PropertyType));
    }
}

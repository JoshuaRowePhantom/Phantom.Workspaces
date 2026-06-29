namespace Phantom.Workspaces.ViewModels;

public sealed class EntityCardViewResolver
{
    public const string RawViewName = "raw";

    public string ResolveViewName(
        SubscribedEntityViewModel entity,
        string? requestedViewName = null)
    {
        if (!string.Equals(requestedViewName, RawViewName, System.StringComparison.Ordinal)
            && entity.IsEntityType("external"))
        {
            return "external";
        }

        return RawViewName;
    }
}

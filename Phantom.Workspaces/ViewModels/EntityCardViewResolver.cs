namespace Phantom.Workspaces.ViewModels;

public sealed class EntityCardViewResolver
{
    public const string RawViewName = "raw";

    public string ResolveViewName(
        SubscribedEntityViewModel entity,
        string? requestedViewName = null)
    {
        if (string.Equals(requestedViewName, RawViewName, System.StringComparison.Ordinal))
        {
            return RawViewName;
        }

        if (entity.IsEntityType("external") && entity.IsEntityType("note"))
        {
            return "external-note";
        }

        if (entity.IsEntityType("external"))
        {
            return "external";
        }

        return RawViewName;
    }
}

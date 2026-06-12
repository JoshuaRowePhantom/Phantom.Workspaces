namespace Phantom.Workspaces.ViewModels;

public sealed class EntityCardViewResolver
{
    public const string RawViewName = "raw";

    public string ResolveViewName(
        SubscribedEntityViewModel entity,
        string? requestedViewName = null)
    {
        _ = entity;
        return string.Equals(requestedViewName, RawViewName, System.StringComparison.Ordinal)
            ? RawViewName
            : RawViewName;
    }
}

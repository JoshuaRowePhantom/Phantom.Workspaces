namespace Phantom.Workspaces.Data;

public sealed record WorkspaceEntitySession
{
    public required EntityId UserEntityId { get; init; }

    public required EntityId ComputerEntityId { get; init; }

    public required EntityId UserComputerProfileEntityId { get; init; }
}

public static class WorkspaceEntityMetaVariables
{
    public const string User = "${USER}";

    public const string Computer = "${COMPUTER}";

    public const string UserProfile = "${USERPROFILE}";
}

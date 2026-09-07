using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Tools;

public static class VsCodeTunnelEntityNaming
{
    public const string TunnelSegment = "vscode-tunnel";

    public static EntityName BuildTunnelName(EntityName profileName)
    {
        return new EntityName([.. profileName.Components, TunnelSegment]);
    }
}

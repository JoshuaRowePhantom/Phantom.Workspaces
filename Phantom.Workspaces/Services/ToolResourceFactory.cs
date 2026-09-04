using System.Collections.Generic;
using System.Linq;
using AgentSchema;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Services;

/// <summary>
/// Composes the MCP-server tool-resource resolution chain for a launch. Extracted from the GUI
/// <c>AgentSessionShortcutContext</c> (issue #1403) so the ViewModels layer no longer knows the
/// MCP search-prefix precedence. Preserves the machine &gt; <c>${USER}/mcp-servers</c> &gt;
/// <c>defaults/mcp-servers</c> ordering (issue #1399): the session data-access layer binds
/// <c>${USER}</c> to the concrete user prefix, matching the UI create flow, and the machine profile
/// is searched first so machine registrations win over user and global ones.
/// </summary>
public static class ToolResourceFactory
{
    /// <summary>
    /// Builds the composing tool-resource factory (fixed built-in tools plus mcp-server-entity
    /// resolution) for the supplied data-access layer and execution identity.
    /// </summary>
    public static IToolResourceFactory CreateMcpServerResolution(
        IDataAccessLayer dataAccessLayer,
        string userName,
        string effectiveComputerName)
    {
        var machineProfilePrefix = new EntityName(
            "computer-user-profiles",
            "users",
            "username",
            userName,
            "computers",
            "hostname",
            effectiveComputerName,
            "copilot",
            "mcp-servers");

        return new ComposingToolResourceFactory(
            new FixedToolResourceFactory(CreateFixedToolMapping()),
            new McpServerEntityToolResourceFactory(
                dataAccessLayer,
                [
                    machineProfilePrefix,
                    // ${USER}/mcp-servers is the mcp-server entity-type's default creation location
                    // (its default-name-prefixes), so the UI create flow places servers here. The
                    // session data-access layer binds ${USER} to the concrete user prefix, matching
                    // the create flow. Searched after the machine profile but before global defaults
                    // so machine > user > global precedence is preserved (issue #1399).
                    new EntityName(WorkspaceEntityMetaVariables.User, "mcp-servers"),
                    new EntityName("defaults", "mcp-servers"),
                ]));
    }

    private static IReadOnlyDictionary<(string Id, string Name), Tool> CreateFixedToolMapping()
    {
        return FixedToolResources.DefaultNames.ToDictionary(
            name => (FixedToolResources.FixedToolResourceId, name),
            name => (Tool)new CustomTool { Kind = name, Name = name });
    }
}

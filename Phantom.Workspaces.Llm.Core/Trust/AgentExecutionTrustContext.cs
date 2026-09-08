namespace Phantom.Workspaces.Llm.Trust;

/// <summary>
/// A session-scoped reference to a trust profile stored on the launch host, together with the
/// entity revision the caller observed. Used on the remote-MCP wire (issue #1477) so the launch
/// host — not the caller — resolves, composes, and compiles the effective profile, and can reject
/// launches when the referenced profile has moved on.
/// </summary>
public sealed record AgentExecutionTrustProfileReference(
    string Kind,
    string Id,
    string? ExpectedRevision);

/// <summary>
/// Per-session shared execution trust context threaded through MCP providers (issue #1477).
/// On the launch host it carries the effective <see cref="TrustProfile"/> and lazily caches its
/// compiled <see cref="TrustProfileProcessPolicyCompilation"/> so the compiler runs exactly once
/// per session. On a client that is only dispatching to a remote host it carries the trust-profile
/// reference / expected revision that the remote host must resolve and compile locally.
/// </summary>
public sealed class AgentExecutionTrustContext
{
    private readonly ITrustProfileProcessPolicyCompiler? compiler;
    private readonly object cacheLock = new();
    private TrustProfileProcessPolicyCompilation? cached;

    /// <summary>
    /// Create a launch-host context that owns an effective trust profile and can compile it into a
    /// portable MXC policy on demand.
    /// </summary>
    public AgentExecutionTrustContext(
        TrustProfile effectiveProfile,
        ITrustProfileProcessPolicyCompiler compiler,
        AgentExecutionTrustProfileReference? remoteReference = null)
    {
        ArgumentNullException.ThrowIfNull(effectiveProfile);
        ArgumentNullException.ThrowIfNull(compiler);
        EffectiveProfile = effectiveProfile;
        this.compiler = compiler;
        RemoteReference = remoteReference;
    }

    /// <summary>
    /// Create a dispatch-only context that carries the reference plus expected revision of the
    /// trust profile the remote launch host must resolve and compile.
    /// </summary>
    public AgentExecutionTrustContext(AgentExecutionTrustProfileReference remoteReference)
    {
        ArgumentNullException.ThrowIfNull(remoteReference);
        RemoteReference = remoteReference;
    }

    /// <summary>The locally resolved effective profile, or null when this session is dispatch-only.</summary>
    public TrustProfile? EffectiveProfile { get; }

    /// <summary>
    /// The remote-dispatch profile reference (kind, id, expected revision), or null when the
    /// session is executing locally on the launch host.
    /// </summary>
    public AgentExecutionTrustProfileReference? RemoteReference { get; }

    /// <summary>True if this context can compile a policy on this host (i.e. is the launch host).</summary>
    public bool IsLaunchHost => EffectiveProfile is not null && compiler is not null;

    /// <summary>
    /// Compile — or return the cached compilation of — the effective profile on this host. Throws
    /// when this context is dispatch-only, because a client must not compile the launch host's
    /// policy.
    /// </summary>
    public TrustProfileProcessPolicyCompilation Compile()
    {
        if (EffectiveProfile is null || compiler is null)
        {
            throw new InvalidOperationException(
                "This AgentExecutionTrustContext is dispatch-only; the launch host must compile the policy.");
        }

        if (cached is { } already)
            return already;

        lock (cacheLock)
        {
            cached ??= compiler.Compile(EffectiveProfile);
            return cached;
        }
    }
}

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
    private readonly IRemoteTrustProfileResolver? resolver;
    private readonly object cacheLock = new();
    private TrustProfileProcessPolicyCompilation? cached;
    private Task<TrustProfileProcessPolicyCompilation>? compilationTask;

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

    /// <summary>
    /// Create a launch-host context from persisted, revision-pinned intent. Resolution and
    /// compilation are deferred until a component actually launches on this host.
    /// </summary>
    public AgentExecutionTrustContext(
        AgentExecutionTrustProfileReference remoteReference,
        IRemoteTrustProfileResolver resolver,
        ITrustProfileProcessPolicyCompiler compiler)
    {
        ArgumentNullException.ThrowIfNull(remoteReference);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(compiler);
        RemoteReference = remoteReference;
        this.resolver = resolver;
        this.compiler = compiler;
    }

    /// <summary>The locally resolved effective profile, or null when this session is dispatch-only.</summary>
    public TrustProfile? EffectiveProfile { get; }

    /// <summary>
    /// The remote-dispatch profile reference (kind, id, expected revision), or null when the
    /// session is executing locally on the launch host.
    /// </summary>
    public AgentExecutionTrustProfileReference? RemoteReference { get; }

    /// <summary>True if this context can compile a policy on this host (i.e. is the launch host).</summary>
    public bool IsLaunchHost => compiler is not null && (EffectiveProfile is not null || resolver is not null);

    /// <summary>
    /// Resolve and compile once on the launch host. Concurrent callers share both successful and
    /// failed work; cancelling a caller only stops that caller's wait.
    /// </summary>
    public async ValueTask<TrustProfileProcessPolicyCompilation> GetCompilationAsync(
        CancellationToken cancellationToken = default)
    {
        Task<TrustProfileProcessPolicyCompilation> task;
        lock (this.cacheLock)
        {
            task = this.compilationTask ??= this.CreateCompilationTask();
        }

        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Compile — or return the cached compilation of — the effective profile on this host. Throws
    /// when this context is dispatch-only, because a client must not compile the launch host's
    /// policy.
    /// </summary>
    public TrustProfileProcessPolicyCompilation Compile()
    {
        if (cached is { } already)
            return already;

        if (EffectiveProfile is null || compiler is null)
        {
            throw new InvalidOperationException(
                "This AgentExecutionTrustContext is dispatch-only; the launch host must compile the policy.");
        }

        lock (cacheLock)
        {
            cached ??= compiler.Compile(EffectiveProfile);
            return cached;
        }
    }

    private Task<TrustProfileProcessPolicyCompilation> CreateCompilationTask()
    {
        if (this.EffectiveProfile is not null && this.compiler is not null)
        {
            try
            {
                return Task.FromResult(this.Compile());
            }
            catch (Exception exception)
            {
                return Task.FromException<TrustProfileProcessPolicyCompilation>(exception);
            }
        }

        if (this.resolver is null || this.compiler is null || this.RemoteReference is null)
        {
            return Task.FromException<TrustProfileProcessPolicyCompilation>(
                new InvalidOperationException(
                    "This AgentExecutionTrustContext is dispatch-only; "
                    + "the launch host has no trust profile resolver/compiler."));
        }

        return this.ResolveAndCompileAsync();
    }

    private async Task<TrustProfileProcessPolicyCompilation> ResolveAndCompileAsync()
    {
        var reference = this.RemoteReference!;
        var resolved = await this.resolver!
            .ResolveAsync(reference.Id, CancellationToken.None)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Trust profile '{reference.Id}' could not be resolved on the launch host.");

        if (!string.Equals(
                resolved.Revision,
                reference.ExpectedRevision,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Trust profile '{reference.Id}' changed from expected revision "
                + $"'{reference.ExpectedRevision}' to '{resolved.Revision}'. Refusing to launch.");
        }

        var result = this.compiler!.Compile(resolved.Profile);
        lock (this.cacheLock)
        {
            this.cached ??= result;
            return this.cached;
        }
    }
}

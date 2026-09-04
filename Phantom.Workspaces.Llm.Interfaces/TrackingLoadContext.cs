using System;
using System.Collections.Generic;
using AgentSchema;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// A generic <see cref="LoadContext"/> that correlates every constructed object with the exact
/// dictionary it was loaded from (issue #1416). AgentSchema's <see cref="LoadContext"/> only exposes
/// <c>PreProcess(dict) -&gt; dict</c> and <c>PostProcess(object) -&gt; object</c>; <c>PostProcess</c>
/// does not receive the source dictionary. This subclass bridges that gap by tracking loads on a
/// stack and exposing a <c>(result, sourceDict)</c> post-hook.
/// </summary>
/// <remarks>
/// This type carries no MCP knowledge — the <c>McpTool</c> → <see cref="PhantomMcpTool"/> rewrite is
/// wired in exactly one place, <see cref="PhantomAgentSchema.CreateContext"/>. Correlation is correct
/// because every generated <c>Load</c> calls <c>ProcessInput</c> first and <c>ProcessOutput</c> last
/// (strict LIFO nesting), so at each <c>ProcessOutput</c> the stack top is the dictionary that
/// produced that object. A stack (not a single field) is required because multiple tools — and nested
/// connections — are in flight, and polymorphic loads process the same instance twice.
/// </remarks>
public sealed class TrackingLoadContext : LoadContext
{
    private readonly Stack<Dictionary<string, object?>> pending = new();

    /// <summary>Optional caller-facing pre-hook; shadows the base hook, which is wired to the tracker.</summary>
    public new Func<Dictionary<string, object?>, Dictionary<string, object?>>? PreProcess { get; set; }

    /// <summary>Invoked for every constructed object, paired with the dictionary it was built from.</summary>
    public new Func<object, Dictionary<string, object?>, object>? PostProcess { get; set; }

    public TrackingLoadContext()
    {
        base.PreProcess = this.TrackPreProcess;
        base.PostProcess = this.TrackPostProcess;
    }

    private Dictionary<string, object?> TrackPreProcess(Dictionary<string, object?> data)
    {
        var transformed = this.PreProcess is { } pre ? pre(data) : data;
        this.pending.Push(transformed);
        return transformed;
    }

    private object TrackPostProcess(object result)
    {
        var data = this.pending.Count > 0 ? this.pending.Pop() : null;
        return this.PostProcess is { } post && data is not null ? post(result, data) : result;
    }
}

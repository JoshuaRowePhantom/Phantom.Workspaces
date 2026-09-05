using System;
using System.Collections.Generic;
using System.Text.Json;
using Phantom.Workspaces.Llm.Trust;

namespace Phantom.Workspaces.Llm.Core.Manifest;

/// <summary>
/// Resolves an <see cref="ExecutorResource"/> to a transport <b>connection-descriptor</b>
/// (<see cref="JsonElement"/>) given the recorded launch <c>parameter-selections</c> and trust context
/// (issue #1436, per-component-executor-binding).
/// </summary>
/// <remarks>
/// <para>
/// Reuse-first / no new schema: the resolver returns the <c>type</c>-discriminated connection-descriptor
/// that <c>ITransportFactoryRegistry.ConnectToAsync</c> already dispatches on (<c>local</c>,
/// <c>user-computer-profile</c>, <c>http</c>, <c>reverse-http</c>, …); there is <b>no</b> parallel
/// <c>ExecutorDescriptor</c> type. For the local/profile shapes it <b>delegates</b> to the existing
/// <see cref="ExecutionTargetResolver"/> rather than re-implementing them.
/// </para>
/// <para>
/// The <see cref="ExecutorResource.ParameterStrategy"/> reads the named <c>executor</c> parameter's
/// recorded selection from the typed <c>parameter-selections</c> map (M7) — NOT from the
/// <c>string→string</c> <c>parameter-values</c> text-templating map — and routes it through the selected
/// trust profile (an explicit <c>trust-profile</c> entity composed via <c>TrustProfileComposer</c>, or an
/// implicit trust profile synthesized from the chosen <c>user-computer-profile</c>).
/// </para>
/// </remarks>
public sealed class ExecutorResourceResolver
{
    /// <summary>The <see cref="ExecutorResource.Options"/> key naming the launch parameter for the parameter strategy.</summary>
    public const string ParameterOptionKey = "parameter";

    /// <summary>The <see cref="ExecutorResource.Options"/> key carrying the fixed entity-id for the user-computer-profile-entity strategy.</summary>
    public const string EntityIdOptionKey = "entity-id";

    /// <summary>The <see cref="ExecutorResource.Options"/> key naming the trust-profile for the trust-profile strategy.</summary>
    public const string TrustProfileOptionKey = "trust-profile";

    private readonly ExecutionTargetResolver executionTargetResolver;

    /// <summary>Creates a resolver, optionally reusing a shared <see cref="ExecutionTargetResolver"/>.</summary>
    public ExecutorResourceResolver(ExecutionTargetResolver? executionTargetResolver = null)
    {
        this.executionTargetResolver = executionTargetResolver ?? new ExecutionTargetResolver();
    }

    /// <summary>
    /// Resolves the given <paramref name="resource"/> to a transport connection-descriptor.
    /// </summary>
    /// <param name="resource">The parsed executor resource to resolve.</param>
    /// <param name="parameterSelections">
    /// The typed <c>parameter-selections</c> map (<c>string → JsonElement</c>) recorded at launch; read by
    /// the <see cref="ExecutorResource.ParameterStrategy"/> strategy.
    /// </param>
    /// <param name="trustProfile">
    /// The composed trust profile supplying the <see cref="TrustProfile.DefaultExecutionTarget"/> for the
    /// <see cref="ExecutorResource.TrustProfileStrategy"/> strategy and the <c>trust-profile</c> selection
    /// path of the parameter strategy.
    /// </param>
    /// <exception cref="InvalidOperationException">If the resource cannot be resolved.</exception>
    public JsonElement Resolve(
        ExecutorResource resource,
        IReadOnlyDictionary<string, JsonElement> parameterSelections,
        TrustProfile? trustProfile)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(parameterSelections);

        return resource.Id switch
        {
            ExecutorResource.LocalStrategy => Local(),
            ExecutorResource.ParameterStrategy => ResolveParameter(resource, parameterSelections, trustProfile),
            ExecutorResource.UserComputerProfileEntityStrategy => ResolveUserComputerProfileEntity(resource),
            ExecutorResource.TrustProfileStrategy => ResolveTrustProfile(resource, trustProfile),
            ExecutorResource.ConnectionDescriptorStrategy => ResolveInlineDescriptor(resource),
            _ => throw Unresolved(resource),
        };
    }

    private static JsonElement Local()
    {
        using var document = JsonDocument.Parse("""{"type":"local"}""");
        return document.RootElement.Clone();
    }

    private JsonElement ResolveParameter(
        ExecutorResource resource,
        IReadOnlyDictionary<string, JsonElement> parameterSelections,
        TrustProfile? trustProfile)
    {
        if (!resource.Options.TryGetValue(ParameterOptionKey, out var parameterName)
            || string.IsNullOrWhiteSpace(parameterName))
        {
            throw Unresolved(resource);
        }

        if (!parameterSelections.TryGetValue(parameterName, out var selection))
        {
            throw Unresolved(resource);
        }

        if (ExecutorParameterSelection.TryGetUserComputerProfile(selection, out var entityId)
            && !string.IsNullOrWhiteSpace(entityId))
        {
            // Synthesize the implicit trust profile whose default execution target is the chosen
            // user-computer-profile, then delegate to the existing resolver.
            var implicitProfile = new TrustProfile
            {
                DefaultExecutionTarget = this.executionTargetResolver.ResolveDescriptor(entityId!),
                HostingWorkspacesClientInstances = [entityId!],
            };

            return this.executionTargetResolver.Resolve(implicitProfile);
        }

        if (ExecutorParameterSelection.TryGetTrustProfile(selection, out var trustProfileNameOrId)
            && !string.IsNullOrWhiteSpace(trustProfileNameOrId))
        {
            // The referenced trust-profile entity is composed by the caller (via TrustProfileComposer)
            // and passed in as trustProfile.
            if (trustProfile is null)
            {
                throw Unresolved(resource);
            }

            return this.executionTargetResolver.Resolve(trustProfile);
        }

        throw Unresolved(resource);
    }

    private JsonElement ResolveUserComputerProfileEntity(ExecutorResource resource)
    {
        if (!resource.Options.TryGetValue(EntityIdOptionKey, out var entityId)
            || string.IsNullOrWhiteSpace(entityId))
        {
            throw Unresolved(resource);
        }

        return this.executionTargetResolver.ResolveDescriptor(entityId!);
    }

    private JsonElement ResolveTrustProfile(ExecutorResource resource, TrustProfile? trustProfile)
    {
        if (trustProfile is null)
        {
            throw Unresolved(resource);
        }

        return this.executionTargetResolver.Resolve(trustProfile);
    }

    private static JsonElement ResolveInlineDescriptor(ExecutorResource resource)
    {
        if (resource.ConnectionDescriptor is { } descriptor)
        {
            return descriptor.Clone();
        }

        throw Unresolved(resource);
    }

    private static InvalidOperationException Unresolved(ExecutorResource resource)
        => new($"Executor resource '{resource.Id}:{resource.Name}' could not be resolved.");
}

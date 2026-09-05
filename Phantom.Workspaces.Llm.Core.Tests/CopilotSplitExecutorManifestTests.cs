using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AgentSchema;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Core.Manifest;

namespace Phantom.Workspaces.Llm.Core.Tests;

/// <summary>
/// Covers the default split-executor Copilot manifest + OAuth-local validation (issue #1441,
/// per-component-executor-binding). The default manifest loads; its <c>worker-profile</c> executor
/// parameter resolves; the model (bound via <c>model.options.executor</c>) resolves to the remote
/// worker; the workspace tools and the GitHub web MCP inherit the local session executor; and an
/// interactive-OAuth MCP bound to a non-local executor is rejected.
/// </summary>
public sealed class CopilotSplitExecutorManifestTests
{
    private const string WorkerProfileUuid = "a1b2c3d4-e5f6-7788-99aa-bbccddeeff00";
    private const string ResourceName = "Phantom.Workspaces.Llm.Core.Tests.copilot-split-executor.json";

    private static string LoadManifestJson()
    {
        var assembly = typeof(CopilotSplitExecutorManifestTests).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {ResourceName}");
        using var reader = new StreamReader(stream);
        var entityJson = reader.ReadToEnd();

        using var document = JsonDocument.Parse(entityJson);
        return document.RootElement.GetProperty("manifest").GetRawText();
    }

    private static ExecutorBindings BuildWorkerBindings()
    {
        var manifestJson = LoadManifestJson();
        var resources = ExecutorResource.ParseManifestResources(manifestJson);
        var selections = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["worker-profile"] = ExecutorParameterSelection.ForUserComputerProfile(WorkerProfileUuid),
        };

        return ExecutorBindings.Build(resources, selections, trustProfile: null);
    }

    [Fact]
    public void Manifest_Loads()
    {
        var manifestJson = LoadManifestJson();

        // Loads through PhantomAgentSchema (via AgentManifestLoader), which strips the executor resource
        // and schema-validates the rest.
        var manifest = AgentManifestLoader.LoadManifestFromJson(manifestJson);
        Assert.NotNull(manifest);

        // The single kind:"executor" resource is the parameter-strategy 'worker'.
        var executors = ExecutorResource.ParseManifestResources(manifestJson);
        var worker = Assert.Single(executors);
        Assert.Equal("worker", worker.Name);
        Assert.Equal(ExecutorResource.ParameterStrategy, worker.Id);
        Assert.Equal("worker-profile", Assert.Contains("parameter", worker.Options));
    }

    [Fact]
    public void Manifest_WorkerProfileParameter_Resolves()
    {
        var manifest = AgentManifestLoader.LoadManifestFromJson(LoadManifestJson());

        var workerProfile = Assert.Single(
            manifest.Parameters?.Properties ?? [],
            property => string.Equals(property.Name, "worker-profile", StringComparison.Ordinal));
        Assert.Equal(AgentManifestParameterKinds.Executor, workerProfile.Kind);
        Assert.True(AgentManifestParameterKinds.IsExecutor(workerProfile.Kind));

        // With a user-computer-profile selection recorded for the worker-profile parameter, the worker
        // executor resolves to that machine's connection-descriptor.
        var bindings = BuildWorkerBindings();
        var worker = bindings.Bindings["worker"];
        Assert.Equal("user-computer-profile", worker.GetProperty("type").GetString());
        Assert.Equal(WorkerProfileUuid, worker.GetProperty("entity-id").GetString());
    }

    [Fact]
    public void Manifest_ModelBoundToWorker_ResolvesRemote()
    {
        using var document = JsonDocument.Parse(LoadManifestJson());
        var executorName = document.RootElement
            .GetProperty("template")
            .GetProperty("model")
            .GetProperty("options")
            .GetProperty("additionalProperties")
            .GetProperty("executor")
            .GetString();
        Assert.Equal("worker", executorName);

        // The model's bound executor resolves to the remote worker (non-local descriptor).
        var descriptor = BuildWorkerBindings().ResolveComponent(executorName);
        Assert.Equal("user-computer-profile", descriptor.GetProperty("type").GetString());
        Assert.Equal(WorkerProfileUuid, descriptor.GetProperty("entity-id").GetString());
        Assert.True(OAuthExecutorBindingValidator.IsNonLocalExecutor(descriptor));
    }

    [Fact]
    public void Manifest_WorkspaceToolsAndGithubWebMcp_ResolveLocal()
    {
        using var document = JsonDocument.Parse(LoadManifestJson());
        var resources = document.RootElement.GetProperty("resources").EnumerateArray().ToArray();

        // None of the tool resources (workspace-entity / workspace-gui / github web MCP) declare an
        // executor, so each inherits the local session executor.
        foreach (var toolName in new[] { "workspace-entity", "workspace-gui", "github" })
        {
            var toolResource = Assert.Single(
                resources,
                resource => resource.GetProperty("kind").GetString() == "tool"
                    && resource.GetProperty("name").GetString() == toolName);
            Assert.False(
                toolResource.TryGetProperty("executor", out _),
                $"Tool '{toolName}' must not declare an executor (it inherits local).");
        }

        // An unset executor resolves to the local session executor.
        var local = BuildWorkerBindings().ResolveComponent(null);
        Assert.Equal("local", local.GetProperty("type").GetString());
        Assert.False(OAuthExecutorBindingValidator.IsNonLocalExecutor(local));
    }

    [Fact]
    public void Validation_OAuthInteractiveMcpWithNonLocalExecutor_IsRejected()
    {
        var local = ExecutorBindings.LocalDescriptor();
        var remoteDescriptor = BuildWorkerBindings().Bindings["worker"];
        var interactiveOAuthTool = new PhantomMcpTool
        {
            ServerName = "github",
            Connection = new OAuthConnection { Endpoint = "https://api.githubcopilot.com/mcp/" },
        };

        // Interactive OAuth + non-local executor is rejected.
        var exception = Assert.Throws<InvalidOperationException>(
            () => OAuthExecutorBindingValidator.EnsureValid(interactiveOAuthTool, remoteDescriptor));
        Assert.Contains("interactive OAuth", exception.Message, StringComparison.Ordinal);
        Assert.Contains("local executor", exception.Message, StringComparison.Ordinal);

        // Interactive OAuth pinned local is fine (the default manifest's configuration).
        Assert.Null(OAuthExecutorBindingValidator.Validate(interactiveOAuthTool, local));

        // The non-interactive host-pinned entra-pinned mode is not rejected even on a remote executor.
        var entraPinnedTool = new PhantomMcpTool
        {
            ServerName = "entra-mcp",
            Connection = new OAuthConnection
            {
                Endpoint = "https://api.example/mcp/",
                AuthenticationMode = PhantomAgentSchema.EntraPinnedAuthenticationMode,
            },
        };
        Assert.Null(OAuthExecutorBindingValidator.Validate(entraPinnedTool, remoteDescriptor));

        // A key/anonymous connection is never interactive OAuth.
        var keyTool = new PhantomMcpTool
        {
            ServerName = "key-mcp",
            Connection = new ApiKeyConnection { Endpoint = "https://api.example/mcp/", ApiKey = "${TOKEN}" },
        };
        Assert.Null(OAuthExecutorBindingValidator.Validate(keyTool, remoteDescriptor));
    }
}

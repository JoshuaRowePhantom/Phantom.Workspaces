using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Core.Manifest;

namespace Phantom.Workspaces.Llm.Core.Tests;

/// <summary>
/// Guards the LLM-facing executor authoring guide (issue #1442, per-component-executor-binding): every
/// fenced JSON example embedded in <c>agent-manifest-executors.md</c> is real and cannot drift from the
/// schema/model. Each example is tagged with an invisible <c>&lt;!-- example: &lt;kind&gt; &lt;group&gt; --&gt;</c>
/// marker so the guard can classify manifests, sessions, and standalone descriptors/selections
/// deterministically.
/// </summary>
public sealed class ExecutorAuthoringGuideTests
{
    private const string ResourceName = "Phantom.Workspaces.Llm.Core.Tests.agent-manifest-executors.md";

    private sealed record GuideExample(string Kind, string Group, string Json);

    private static string LoadGuideText()
    {
        var assembly = typeof(ExecutorAuthoringGuideTests).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {ResourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static IReadOnlyList<GuideExample> LoadExamples()
    {
        var text = LoadGuideText();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var examples = new List<GuideExample>();

        string? pendingKind = null;
        string? pendingGroup = null;
        var insideFence = false;
        var builder = new StringBuilder();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (!insideFence
                && trimmed.StartsWith("<!-- example:", StringComparison.Ordinal)
                && trimmed.EndsWith("-->", StringComparison.Ordinal))
            {
                var inner = trimmed["<!-- example:".Length..^"-->".Length].Trim();
                var parts = inner.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                pendingKind = parts.Length > 0 ? parts[0] : null;
                pendingGroup = parts.Length > 1 ? parts[1] : "-";
                continue;
            }

            if (!insideFence && trimmed.StartsWith("```json", StringComparison.Ordinal))
            {
                // Only capture fences preceded by an example marker.
                if (pendingKind is null)
                {
                    continue;
                }

                insideFence = true;
                builder.Clear();
                continue;
            }

            if (insideFence && trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                insideFence = false;
                examples.Add(new GuideExample(pendingKind!, pendingGroup ?? "-", builder.ToString()));
                pendingKind = null;
                pendingGroup = null;
                continue;
            }

            if (insideFence)
            {
                builder.AppendLine(line);
            }
        }

        return examples;
    }

    private static bool JsonEquals(JsonElement left, JsonElement right)
        => JsonNode.DeepEquals(JsonNode.Parse(left.GetRawText()), JsonNode.Parse(right.GetRawText()));

    [Fact]
    public void AuthoringGuide_EmbeddedManifestExamples_ParseAndRoundTrip()
    {
        var manifests = LoadExamples().Where(example => example.Kind == "manifest").ToArray();
        Assert.NotEmpty(manifests);

        foreach (var example in manifests)
        {
            // Every manifest example loads through PhantomAgentSchema (via AgentManifestLoader), which
            // schema-validates the full document and strips executor resources before deserialisation.
            var manifest = AgentManifestLoader.LoadManifestFromJson(example.Json);
            Assert.NotNull(manifest);

            // Every executor resource it declares round-trips parse -> serialise -> parse losslessly,
            // including the inline connection-descriptor escape hatch.
            foreach (var executor in ExecutorResource.ParseManifestResources(example.Json))
            {
                var firstNode = executor.ToResourceNode();
                var reparsed = ExecutorResource.FromResourceElement(
                    JsonSerializer.Deserialize<JsonElement>(firstNode.ToJsonString()));
                var secondNode = reparsed.ToResourceNode();
                Assert.True(
                    JsonNode.DeepEquals(firstNode, secondNode),
                    $"Executor resource '{executor.Name}' in group '{example.Group}' did not round-trip losslessly.");
            }
        }
    }

    [Fact]
    public void AuthoringGuide_EmbeddedSessionExamples_ResolveToDescriptors()
    {
        var examples = LoadExamples();
        var manifestsByGroup = examples
            .Where(example => example.Kind == "manifest")
            .ToDictionary(example => example.Group, example => example.Json, StringComparer.Ordinal);
        var sessions = examples.Where(example => example.Kind == "session").ToArray();
        Assert.NotEmpty(sessions);

        var pairedGroups = new List<string>();

        foreach (var session in sessions)
        {
            Assert.True(
                manifestsByGroup.TryGetValue(session.Group, out var manifestJson),
                $"Session example group '{session.Group}' has no paired manifest example.");
            pairedGroups.Add(session.Group);

            using var sessionDocument = JsonDocument.Parse(session.Json);
            var root = sessionDocument.RootElement;

            var parameterSelections = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (root.TryGetProperty("parameter-selections", out var selections)
                && selections.ValueKind == JsonValueKind.Object)
            {
                foreach (var selection in selections.EnumerateObject())
                {
                    parameterSelections[selection.Name] = selection.Value.Clone();
                }
            }

            var executorBindingsElement = root.GetProperty("executor-bindings");
            var sessionExecutor = executorBindingsElement.GetProperty("session").Clone();
            var components = executorBindingsElement.GetProperty("components");

            // Resolve the paired manifest's executor resources with the session's recorded selections and
            // assert the bindings equal the exact connection-descriptors the guide persists.
            var resources = ExecutorResource.ParseManifestResources(manifestJson!);
            var bindings = ExecutorBindings.Build(
                resources,
                parameterSelections,
                trustProfile: null,
                resolver: null,
                sessionExecutor: sessionExecutor);

            var componentCount = 0;
            foreach (var component in components.EnumerateObject())
            {
                componentCount++;
                Assert.True(
                    bindings.Bindings.ContainsKey(component.Name),
                    $"Session '{session.Group}' component '{component.Name}' was not produced by the manifest resources.");
                Assert.True(
                    JsonEquals(bindings.Bindings[component.Name], component.Value),
                    $"Session '{session.Group}' component '{component.Name}' did not resolve to the documented descriptor.");
            }

            Assert.Equal(componentCount, bindings.Bindings.Count);

            // An unset component inherits the session executor exactly as the guide claims.
            Assert.True(
                JsonEquals(bindings.ResolveComponent(null), sessionExecutor),
                $"Session '{session.Group}' unset-executor fallback did not resolve to the documented session executor.");
        }

        // The two worked end-to-end examples the issue requires must both be exercised.
        Assert.Contains("split", pairedGroups, StringComparer.Ordinal);
        Assert.Contains("local", pairedGroups, StringComparer.Ordinal);
    }

    [Fact]
    public void AuthoringGuide_DocumentsAllFiveIdStrategies()
    {
        var text = LoadGuideText();

        foreach (var strategy in new[]
        {
            ExecutorResource.LocalStrategy,
            ExecutorResource.ParameterStrategy,
            ExecutorResource.UserComputerProfileEntityStrategy,
            ExecutorResource.TrustProfileStrategy,
            ExecutorResource.ConnectionDescriptorStrategy,
        })
        {
            Assert.Contains(strategy, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AuthoringGuide_ExecutorParameterValueShapes_Documented()
    {
        var text = LoadGuideText().Replace(" ", string.Empty);

        Assert.Contains("\"trust-profile\":", text, StringComparison.Ordinal);
        Assert.Contains("\"user-computer-profile\":", text, StringComparison.Ordinal);
    }
}

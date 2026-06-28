using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using AgentSchema;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// When changing substitution rules or well-known parameter names, update the workspace
/// documentation entity: <c>["documentation", "agent-options", "parameters"]</c>.
/// </summary>
public static class AgentDefinitionParameterSubstitutor
{
    public static AgentDefinition Substitute(
        AgentManifest manifest,
        IReadOnlyDictionary<string, string>? parameterValues)
    {
        var resolvedValues = ResolveParameterValues(manifest, parameterValues);

        var template = manifest.Template
            ?? throw new InvalidOperationException("Agent manifest does not specify a template agent definition.");
        var definition = AgentDefinition.FromJson(template.ToJson())
            ?? throw new InvalidOperationException("Failed to clone the agent manifest template.");

        if (definition is PromptAgent promptAgent
            && promptAgent.Model?.Options?.AdditionalProperties is { } additionalProps
            && resolvedValues.Count > 0)
        {
            foreach (var key in new List<string>(additionalProps.Keys))
            {
                if (additionalProps[key] is string strValue)
                {
                    additionalProps[key] = SubstitutePlaceholders(strValue, resolvedValues);
                }
            }
        }

        return definition;
    }

    private static IReadOnlyDictionary<string, string> ResolveParameterValues(
        AgentManifest manifest,
        IReadOnlyDictionary<string, string>? parameterValues)
    {
        var parameters = manifest.Parameters?.Properties;
        if (parameters is null || parameters.Count == 0)
        {
            return parameterValues ?? new Dictionary<string, string>();
        }

        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var param in parameters)
        {
            var name = param.Name;
            if (name is null) continue;

            string? value = null;
            if (parameterValues?.TryGetValue(name, out var providedValue) == true)
            {
                value = providedValue;
            }
            else if (param.Default is string defaultStr)
            {
                value = defaultStr;
            }
            else if (param.Default is not null)
            {
                value = param.Default.ToString();
            }

            if (value is null && param.Required == true)
            {
                throw new ArgumentException(
                    $"Required parameter '{name}' has no value and no default.",
                    nameof(parameterValues));
            }

            if (value is not null)
            {
                resolved[name] = value;
            }
        }

        return resolved;
    }

    private static string SubstitutePlaceholders(
        string value,
        IReadOnlyDictionary<string, string> resolvedValues)
    {
        return Regex.Replace(value, @"\$\{([^}]+)\}", match =>
        {
            var paramName = match.Groups[1].Value;
            return resolvedValues.TryGetValue(paramName, out var replacement)
                ? replacement
                : match.Value;
        });
    }
}

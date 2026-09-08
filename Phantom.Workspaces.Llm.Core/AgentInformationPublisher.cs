using AgentSchema;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// #1485: owner-side validation and construction helpers for <see cref="AgentInformation"/>
/// publications. Required strings must be non-blank; <see cref="AgentInformation.CurrentModelId"/>
/// is either <see langword="null"/> or non-blank; <see cref="AgentInformation.AgentDefinition"/>
/// must be non-null.
/// </summary>
public static class AgentInformationPublisher
{
    /// <summary>Validates a candidate <see cref="AgentInformation"/> value.</summary>
    public static bool TryValidate(AgentInformation candidate, out string? errorCode)
    {
        if (string.IsNullOrWhiteSpace(candidate.AgentSessionId)
            || string.IsNullOrWhiteSpace(candidate.AgentId)
            || string.IsNullOrWhiteSpace(candidate.Name)
            || string.IsNullOrWhiteSpace(candidate.DisplayName)
            || string.IsNullOrWhiteSpace(candidate.Description))
        {
            errorCode = "blank-required-string";
            return false;
        }
        if (candidate.CurrentModelId is not null && string.IsNullOrWhiteSpace(candidate.CurrentModelId))
        {
            errorCode = "blank-optional-model";
            return false;
        }
        if (candidate.AgentDefinition is null)
        {
            errorCode = "null-definition";
            return false;
        }
        errorCode = null;
        return true;
    }

    /// <summary>
    /// Produces a fallback description when the intended value is blank. Prefers a non-blank
    /// display-name; when both description and display-name are blank returns the caller-supplied
    /// <paramref name="ultimateFallback"/>, which must itself be non-blank.
    /// </summary>
    public static string EnsureNonBlankDescription(string? description, string? displayName, string ultimateFallback)
    {
        if (!string.IsNullOrWhiteSpace(description))
        {
            return description!;
        }
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            return displayName!;
        }
        if (string.IsNullOrWhiteSpace(ultimateFallback))
        {
            throw new ArgumentException("Ultimate fallback must be non-blank.", nameof(ultimateFallback));
        }
        return ultimateFallback;
    }
}

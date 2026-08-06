namespace Phantom.Workspaces.Llm.Secrets;

/// <summary>Why a secret could not be obtained.</summary>
public enum SecretRequestFailureReason
{
    /// <summary>The secret does not exist in the platform store.</summary>
    DoesntExist,

    /// <summary>The secret exists but could not be read (e.g. an access error).</summary>
    ErrorReading,

    /// <summary>Any other failure.</summary>
    Other,
}

/// <summary>
/// Describes a failure to obtain a secret. Holds no secret value — only a display string.
/// </summary>
public sealed record SecretRequestFailure(
    string SecretName,
    string FailureReasonDisplayString,
    SecretRequestFailureReason Reason);

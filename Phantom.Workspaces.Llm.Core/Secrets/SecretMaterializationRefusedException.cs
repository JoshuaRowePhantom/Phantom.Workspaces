namespace Phantom.Workspaces.Llm.Secrets;

/// <summary>
/// Thrown when the user explicitly refuses to materialize one or more secrets.
/// </summary>
public sealed class SecretMaterializationRefusedException : Exception
{
    public SecretMaterializationRefusedException(string message)
        : base(message)
    {
    }

    public SecretMaterializationRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

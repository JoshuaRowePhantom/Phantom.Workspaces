using System.Security.Cryptography;
using System.Text;

namespace Phantom.Workspaces.Data;

public static class DeterministicEntityId
{
    /// <summary>
    /// Creates a deterministic <see cref="EntityId"/> by hashing the supplied <paramref name="inputs"/>,
    /// joined with "/" separators, as UTF-8 bytes via MD5.
    /// Same inputs always produce the same <see cref="EntityId"/>.
    /// </summary>
    public static EntityId Create(params string[] inputs)
    {
        var canonical = string.Join("/", inputs);
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(canonical));
        return new EntityId(new Guid(hash));
    }
}

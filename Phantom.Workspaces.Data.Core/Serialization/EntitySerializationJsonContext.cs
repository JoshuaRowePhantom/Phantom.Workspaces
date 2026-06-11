using System.Text.Json.Serialization;

namespace Phantom.Workspaces.Data.Serialization;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(Dictionary<string, MimeAttachmentDocument>))]
[JsonSerializable(typeof(CoreSortFieldDocument))]
[JsonSerializable(typeof(CoreTimestampDocument))]
[JsonSerializable(typeof(InlineContentDocument))]
[JsonSerializable(typeof(MimeAttachmentDocument))]
[JsonSerializable(typeof(NoteEntityDocument))]
[JsonSerializable(typeof(SchemaEntityDocument))]
[JsonSerializable(typeof(SchemaPayloadDocument))]
public partial class EntitySerializationJsonContext : JsonSerializerContext
{
}

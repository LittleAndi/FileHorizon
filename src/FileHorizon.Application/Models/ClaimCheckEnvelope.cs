using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileHorizon.Application.Models;

/// <summary>
/// The message body sent in place of file content when a blob destination is claim-checked:
/// <c>{"blobUrl":"...","contentType":"...","length":123}</c>.
/// </summary>
/// <remarks>
/// The shape is an interop contract with consumers written independently of this repo, so the property
/// names are fixed and camelCase. A consumer identifies an envelope by the <c>claimCheck</c> application
/// property rather than by sniffing the body, which is why the message keeps the file's real content type
/// rather than advertising itself as application/json.
///
/// <c>contentType</c> is omitted entirely when the blob destination does not resolve a type
/// (<c>ContentTypeStrategy: None</c>), rather than serialized as <c>null</c>, so a consumer can bind the
/// property to a non-nullable string.
/// </remarks>
public sealed record ClaimCheckEnvelope(
    [property: JsonPropertyName("blobUrl")] string BlobUrl,
    [property: JsonPropertyName("contentType")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ContentType,
    [property: JsonPropertyName("length")] long Length)
{
    /// <summary>Application property that marks a Service Bus message as carrying a pointer, not a payload.</summary>
    public const string ApplicationPropertyName = "claimCheck";

    public byte[] ToUtf8Json() => JsonSerializer.SerializeToUtf8Bytes(this);
}

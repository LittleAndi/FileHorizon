namespace FileHorizon.Application.Models;

/// <summary>
/// What a sink produced for a successful write: how many bytes landed and, when the sink can name them,
/// where and as what. <see cref="Location"/> is what makes a claim-check pointer possible — the Azure Blob
/// sink fills it with the blob URI so a downstream publisher can reference the artifact instead of copying
/// it, and <see cref="ContentType"/> so the pointer reports the type the consumer will actually fetch.
/// </summary>
public sealed record FileWriteReceipt(long BytesWritten, Uri? Location = null, string? ContentType = null);

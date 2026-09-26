namespace DataHub.Application.Abstractions;

public sealed record StoredObject(Stream Content, string ContentType, long SizeBytes) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

public sealed record ObjectInfo(string Key, long SizeBytes, DateTimeOffset? LastModifiedUtc);

public interface IObjectStorage
{
    Task PutAsync(string bucket, string key, Stream content, string contentType, CancellationToken ct = default);

    /// <summary>Stream and metadata, or null when the object does not exist.</summary>
    Task<StoredObject?> GetAsync(string bucket, string key, CancellationToken ct = default);

    /// <summary>Metadata only (HEAD), or null when missing.</summary>
    Task<ObjectInfo?> HeadAsync(string bucket, string key, CancellationToken ct = default);

    Task<IReadOnlyList<ObjectInfo>> ListAsync(string bucket, string? prefix = null, CancellationToken ct = default);

    Task DeleteAsync(string bucket, string key, CancellationToken ct = default);
}

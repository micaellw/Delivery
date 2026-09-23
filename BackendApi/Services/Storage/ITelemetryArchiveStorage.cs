using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BackendApi.Services.Storage
{
    public interface ITelemetryArchiveStorage
    {
        string BucketName { get; }
        Task EnsureArchiveBucketExistsAsync(CancellationToken ct = default);
        Task<string> PutArchiveObjectAsync(Stream dataStream, string objectKey, string contentType = "application/x-ndjson", CancellationToken ct = default);
        Task<bool> ObjectExistsAsync(string objectKey, CancellationToken ct = default);
        Task GetArchiveObjectAsync(string objectKey, Action<Stream> callback, CancellationToken ct = default);
    }
}

using System.Globalization;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BackendApi.Services.Storage
{
    public class MinioTelemetryArchiveStorage : ITelemetryArchiveStorage
    {
        static MinioTelemetryArchiveStorage()
        {
            // CRITICAL: Guarantee Invariant Culture for AWS S3 Signature v4 across all threads
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        }
        private readonly IMinioClient _minioClient;
        private readonly ILogger<MinioTelemetryArchiveStorage> _logger;
        private readonly string _bucketName;
        private volatile bool _bucketVerified;

        public string BucketName => _bucketName;

        public MinioTelemetryArchiveStorage(IConfiguration config, ILogger<MinioTelemetryArchiveStorage> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _bucketName = config["Minio:TelemetryBucketName"] 
                ?? config["TELEMETRY_ARCHIVE_BUCKET_NAME"] 
                ?? "delivery-telemetry-archive";

            var endpoint = config["Minio:Endpoint"] ?? "minio:9000";
            var accessKey = config["Minio:AccessKey"];
            var secretKey = config["Minio:SecretKey"];
            var useSsl = config.GetValue<bool>("Minio:UseSsl");

            var builder = new MinioClient()
                .WithEndpoint(endpoint)
                .WithSSL(useSsl);

            var effectiveAccessKey = string.IsNullOrWhiteSpace(accessKey) ? "minioadmin" : accessKey;
            var effectiveSecretKey = string.IsNullOrWhiteSpace(secretKey) ? "miniopassword123" : secretKey;
            builder = builder.WithCredentials(effectiveAccessKey, effectiveSecretKey);

            _minioClient = builder.Build();
        }

        // Test constructor for unit testing / mocking
        public MinioTelemetryArchiveStorage(IMinioClient minioClient, string bucketName, ILogger<MinioTelemetryArchiveStorage> logger)
        {
            _minioClient = minioClient ?? throw new ArgumentNullException(nameof(minioClient));
            _bucketName = bucketName ?? throw new ArgumentNullException(nameof(bucketName));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task EnsureArchiveBucketExistsAsync(CancellationToken ct = default)
        {
            try
            {
                bool exists = await _minioClient.BucketExistsAsync(new BucketExistsArgs().WithBucket(_bucketName), ct);
                if (!exists)
                {
                    await _minioClient.MakeBucketAsync(new MakeBucketArgs().WithBucket(_bucketName), ct);
                    // CRITICAL ARCHITECTURE RULE:
                    // Telemetry archive bucket MUST remain strictly private.
                    // DO NOT apply any public-read or anonymous access policy.
                    _logger.LogInformation("MinIO Telemetry Archive Bucket {BucketName} created (strictly private, no public policy).", _bucketName);
                }
                else
                {
                    _logger.LogInformation("MinIO Telemetry Archive Bucket {BucketName} verified existing.", _bucketName);
                }
                _bucketVerified = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize MinIO telemetry archive bucket {BucketName}.", _bucketName);
                throw;
            }
        }

        public async Task<string> PutArchiveObjectAsync(Stream dataStream, string objectKey, string contentType = "application/x-ndjson", CancellationToken ct = default)
        {
            if (dataStream == null) throw new ArgumentNullException(nameof(dataStream));
            if (string.IsNullOrWhiteSpace(objectKey)) throw new ArgumentException("Object key cannot be empty", nameof(objectKey));

            if (!_bucketVerified)
            {
                await EnsureArchiveBucketExistsAsync(ct);
            }

            var putObjectArgs = new PutObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(objectKey)
                .WithStreamData(dataStream)
                .WithObjectSize(dataStream.Length)
                .WithContentType(contentType);

            await _minioClient.PutObjectAsync(putObjectArgs, ct);
            _logger.LogInformation("Archived telemetry batch to MinIO: bucket={Bucket}, key={Key}, size={Size} bytes", _bucketName, objectKey, dataStream.Length);
            return objectKey;
        }

        public async Task<bool> ObjectExistsAsync(string objectKey, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(objectKey)) return false;

            try
            {
                var statArgs = new StatObjectArgs()
                    .WithBucket(_bucketName)
                    .WithObject(objectKey);
                var stat = await _minioClient.StatObjectAsync(statArgs, ct);
                return stat != null;
            }
            catch (Minio.Exceptions.ObjectNotFoundException)
            {
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error checking existence for object {ObjectKey} in bucket {BucketName}", objectKey, _bucketName);
                return false;
            }
        }

        public async Task GetArchiveObjectAsync(string objectKey, Action<Stream> callback, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(objectKey)) throw new ArgumentException("Object key cannot be empty", nameof(objectKey));
            if (callback == null) throw new ArgumentNullException(nameof(callback));

            var getObjectArgs = new GetObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(objectKey)
                .WithCallbackStream(callback);

            await _minioClient.GetObjectAsync(getObjectArgs, ct);
        }
    }
}

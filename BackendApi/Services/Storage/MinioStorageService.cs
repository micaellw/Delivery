using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace BackendApi.Services.Storage
{
    public class MinioStorageService : IStorageService
    {
        private readonly IMinioClient _minioClient;
        private readonly ILogger<MinioStorageService> _logger;
        private readonly string _bucketName;
        private readonly string _publicUrl;

        public MinioStorageService(IConfiguration config, ILogger<MinioStorageService> logger)
        {
            _logger = logger;
            _bucketName = config["Minio:BucketName"] ?? "delivery-media";
            _publicUrl = config["Minio:PublicUrl"] ?? "http://localhost:8088/storage/delivery-media";

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
            // Constructor is now 100% clean and non-blocking (zero synchronous network I/O)
        }

        public async Task EnsureBucketExistsAsync(CancellationToken ct = default)
        {
            try
            {
                bool exists = await _minioClient.BucketExistsAsync(new BucketExistsArgs().WithBucket(_bucketName), ct);
                if (!exists)
                {
                    await _minioClient.MakeBucketAsync(new MakeBucketArgs().WithBucket(_bucketName), ct);
                    
                    var policy = "{\"Version\":\"2012-10-17\",\"Statement\":[{\"Effect\":\"Allow\",\"Principal\":{\"AWS\":[\"*\"]},\"Action\":[\"s3:GetBucketLocation\",\"s3:ListBucket\"],\"Resource\":[\"arn:aws:s3:::" + _bucketName + "\"]},{\"Effect\":\"Allow\",\"Principal\":{\"AWS\":[\"*\"]},\"Action\":[\"s3:GetObject\"],\"Resource\":[\"arn:aws:s3:::" + _bucketName + "/*\"]}]}";
                    await _minioClient.SetPolicyAsync(new SetPolicyArgs().WithBucket(_bucketName).WithPolicy(policy), ct);
                    _logger.LogInformation("MinIO Bucket {BucketName} created with public-read policy.", _bucketName);
                }
                else
                {
                    _logger.LogInformation("MinIO Bucket {BucketName} verified existing.", _bucketName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize MinIO bucket {BucketName}. Service startup continues; requests may retry or degrade.", _bucketName);
            }
        }

        public async Task<string> UploadAsync(Stream stream, string fileName, string contentType, string folder = "items", CancellationToken ct = default)
        {
            var objectName = folder + "/" + Guid.NewGuid() + Path.GetExtension(fileName);
            var putObjectArgs = new PutObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(objectName)
                .WithStreamData(stream)
                .WithObjectSize(stream.Length)
                .WithContentType(contentType);

            await _minioClient.PutObjectAsync(putObjectArgs, ct);
            return objectName;
        }

        public async Task<string> UploadBase64Async(string base64Data, string folder = "items", CancellationToken ct = default)
        {
            var match = Regex.Match(base64Data, @"data:image/(?<type>.+?),(?<data>.+)");
            if (!match.Success) throw new ArgumentException("Invalid base64 string format");

            var ext = match.Groups["type"].Value.Split(';')[0];
            var bytes = Convert.FromBase64String(match.Groups["data"].Value);

            using var stream = new MemoryStream(bytes);
            return await UploadAsync(stream, "file." + ext, "image/" + ext, folder, ct);
        }

        public async Task<bool> DeleteAsync(string objectName, CancellationToken ct = default)
        {
            try
            {
                await _minioClient.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(_bucketName).WithObject(objectName), ct);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete object {ObjectName}", objectName);
                return false;
            }
        }

        public string GetPublicUrl(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return string.Empty;
            if (objectName.StartsWith("http")) return objectName;
            return _publicUrl + "/" + objectName;
        }
    }
}

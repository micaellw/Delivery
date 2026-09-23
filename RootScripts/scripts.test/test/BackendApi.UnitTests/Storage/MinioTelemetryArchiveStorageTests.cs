using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BackendApi.Services.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Minio;
using Minio.DataModel;
using Minio.DataModel.Args;
using Minio.DataModel.Response;
using Minio.Exceptions;
using Moq;
using Xunit;

namespace BackendApi.UnitTests.Storage
{
    public class MinioTelemetryArchiveStorageTests
    {
        private readonly Mock<IMinioClient> _minioClientMock;
        private readonly Mock<ILogger<MinioTelemetryArchiveStorage>> _loggerMock;
        private readonly string _bucketName = "delivery-telemetry-archive";

        public MinioTelemetryArchiveStorageTests()
        {
            _minioClientMock = new Mock<IMinioClient>();
            _loggerMock = new Mock<ILogger<MinioTelemetryArchiveStorage>>();
        }

        [Fact]
        public void BucketName_Property_ReturnsConfiguredBucket()
        {
            // Arrange & Act
            var storage = new MinioTelemetryArchiveStorage(_minioClientMock.Object, _bucketName, _loggerMock.Object);

            // Assert
            Assert.Equal("delivery-telemetry-archive", storage.BucketName);
        }

        [Fact]
        public async Task EnsureArchiveBucketExistsAsync_WhenBucketDoesNotExist_CreatesBucket_AndNeverAppliesPublicPolicy()
        {
            // Arrange
            _minioClientMock
                .Setup(m => m.BucketExistsAsync(It.IsAny<BucketExistsArgs>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);

            _minioClientMock
                .Setup(m => m.MakeBucketAsync(It.IsAny<MakeBucketArgs>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var storage = new MinioTelemetryArchiveStorage(_minioClientMock.Object, _bucketName, _loggerMock.Object);

            // Act
            await storage.EnsureArchiveBucketExistsAsync();

            // Assert: Bucket was created
            _minioClientMock.Verify(m => m.MakeBucketAsync(It.Is<MakeBucketArgs>(args => args != null), It.IsAny<CancellationToken>()), Times.Once);

            // Assert: STRICT PRIVACY CONTRACT - SetPolicyAsync MUST NEVER BE CALLED!
            _minioClientMock.Verify(m => m.SetPolicyAsync(It.IsAny<SetPolicyArgs>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task EnsureArchiveBucketExistsAsync_WhenBucketAlreadyExists_DoesNotCallMakeBucket()
        {
            // Arrange
            _minioClientMock
                .Setup(m => m.BucketExistsAsync(It.IsAny<BucketExistsArgs>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var storage = new MinioTelemetryArchiveStorage(_minioClientMock.Object, _bucketName, _loggerMock.Object);

            // Act
            await storage.EnsureArchiveBucketExistsAsync();

            // Assert
            _minioClientMock.Verify(m => m.MakeBucketAsync(It.IsAny<MakeBucketArgs>(), It.IsAny<CancellationToken>()), Times.Never);
            _minioClientMock.Verify(m => m.SetPolicyAsync(It.IsAny<SetPolicyArgs>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task PutArchiveObjectAsync_UploadsStreamToMinio()
        {
            // Arrange
            _minioClientMock
                .Setup(m => m.BucketExistsAsync(It.IsAny<BucketExistsArgs>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            _minioClientMock
                .Setup(m => m.PutObjectAsync(It.IsAny<PutObjectArgs>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((PutObjectResponse)null!);

            var storage = new MinioTelemetryArchiveStorage(_minioClientMock.Object, _bucketName, _loggerMock.Object);
            var testBytes = Encoding.UTF8.GetBytes("test ndjson content");
            using var stream = new MemoryStream(testBytes);
            var objectKey = "gps/year=2026/month=09/day=21/hour=00/batch_100-0_200-0.ndjson.gz";

            // Act
            var resultKey = await storage.PutArchiveObjectAsync(stream, objectKey, "application/x-ndjson");

            // Assert
            Assert.Equal(objectKey, resultKey);
            _minioClientMock.Verify(m => m.PutObjectAsync(It.Is<PutObjectArgs>(args => args != null), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task PutArchiveObjectAsync_ThrowsWhenStreamIsNull()
        {
            // Arrange
            var storage = new MinioTelemetryArchiveStorage(_minioClientMock.Object, _bucketName, _loggerMock.Object);

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                storage.PutArchiveObjectAsync(null!, "some-key"));
        }

        [Fact]
        public async Task ObjectExistsAsync_WhenObjectFound_ReturnsTrue()
        {
            // Arrange
            var fakeStat = (ObjectStat)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ObjectStat));
            _minioClientMock
                .Setup(m => m.StatObjectAsync(It.IsAny<StatObjectArgs>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(fakeStat);

            var storage = new MinioTelemetryArchiveStorage(_minioClientMock.Object, _bucketName, _loggerMock.Object);

            // Act
            var exists = await storage.ObjectExistsAsync("existing/key.ndjson.gz");

            // Assert
            Assert.True(exists);
        }

        [Fact]
        public async Task ObjectExistsAsync_WhenObjectNotFound_ReturnsFalse()
        {
            // Arrange
            _minioClientMock
                .Setup(m => m.StatObjectAsync(It.IsAny<StatObjectArgs>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ObjectNotFoundException("Object not found"));

            var storage = new MinioTelemetryArchiveStorage(_minioClientMock.Object, _bucketName, _loggerMock.Object);

            // Act
            var exists = await storage.ObjectExistsAsync("missing/key.ndjson.gz");

            // Assert
            Assert.False(exists);
        }

        [Fact]
        public void DependencyInjection_Resolves_BothStorageServicesIndependently()
        {
            // Arrange
            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "ConnectionStrings:DefaultConnection", "Host=localhost;Database=delivery_db;Username=postgres;Password=test" },
                    { "ConnectionStrings:Redis", "localhost:6379" },
                    { "Minio:Endpoint", "localhost:9000" },
                    { "Minio:BucketName", "delivery-media" },
                    { "Minio:TelemetryBucketName", "delivery-telemetry-archive" },
                    { "Minio:PublicUrl", "http://localhost:8088/storage/delivery-media" }
                })
                .Build();

            services.AddSingleton<IConfiguration>(configuration);
            services.AddLogging();
            services.AddSingleton<IStorageService, MinioStorageService>();
            services.AddSingleton<ITelemetryArchiveStorage, MinioTelemetryArchiveStorage>();

            var provider = services.BuildServiceProvider();

            // Act
            var archiveStorage = provider.GetService<ITelemetryArchiveStorage>();
            var mediaStorage = provider.GetService<IStorageService>();

            // Assert: Telemetry Archive Storage resolved with correct bucket
            Assert.NotNull(archiveStorage);
            Assert.IsType<MinioTelemetryArchiveStorage>(archiveStorage);
            Assert.Equal("delivery-telemetry-archive", archiveStorage.BucketName);

            // Assert: Media Storage resolved independently
            Assert.NotNull(mediaStorage);
            Assert.IsType<MinioStorageService>(mediaStorage);
        }
    }
}

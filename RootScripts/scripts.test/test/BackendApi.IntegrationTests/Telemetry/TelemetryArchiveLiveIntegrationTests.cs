using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BackendApi.Services.BackgroundWorkers.Jobs;
using BackendApi.Services.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Minio;
using Minio.DataModel.Args;
using StackExchange.Redis;
using Xunit;

namespace BackendApi.IntegrationTests.Telemetry
{
    public class TelemetryArchiveLiveIntegrationTests
    {
        private const string RedisConnectionString = "127.0.0.1:6389,password=Password123!,abortConnect=false";
        private const string MinioEndpoint = "127.0.0.1:9002";
        private const string MinioAccessKey = "minioadmin";
        private const string MinioSecretKey = "miniopassword123";
        private const string ArchiveBucketName = "delivery-telemetry-archive";

        private readonly IConfiguration _configuration;

        static TelemetryArchiveLiveIntegrationTests()
        {
            // CRITICAL: Guarantee Invariant/Gregorian calendar for AWS S3 Signature v4 on machines with Thai regional culture
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        }

        public TelemetryArchiveLiveIntegrationTests()
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;

            var inMemorySettings = new Dictionary<string, string?>
            {
                ["Minio:Endpoint"] = MinioEndpoint,
                ["Minio:AccessKey"] = MinioAccessKey,
                ["Minio:SecretKey"] = MinioSecretKey,
                ["Minio:UseSsl"] = "false",
                ["Minio:TelemetryBucketName"] = ArchiveBucketName
            };

            _configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(inMemorySettings)
                .Build();
        }

        [Fact]
        public async Task SubStep_2_2_B_2_6_A_LiveNormalArchive_1000Entries_FullRoundTrip_And_PrivacyVerification()
        {
            // =========================================================================
            // STEP 1: Connect to Real Docker Redis and Real Docker MinIO
            // =========================================================================
            var redis = await ConnectionMultiplexer.ConnectAsync(RedisConnectionString);
            var redisDb = redis.GetDatabase();

            var storage = new MinioTelemetryArchiveStorage(_configuration, new NullLogger<MinioTelemetryArchiveStorage>());
            await storage.EnsureArchiveBucketExistsAsync();

            // =========================================================================
            // STEP 2: Verify MinIO Bucket Runtime Privacy (Must be strictly private)
            // =========================================================================
            var rawMinioClient = new MinioClient()
                .WithEndpoint(MinioEndpoint)
                .WithCredentials(MinioAccessKey, MinioSecretKey)
                .WithSSL(false)
                .Build();

            bool isPolicyAbsentOrPrivate = false;
            try
            {
                var policyJson = await rawMinioClient.GetPolicyAsync(new GetPolicyArgs().WithBucket(ArchiveBucketName));
                isPolicyAbsentOrPrivate = string.IsNullOrWhiteSpace(policyJson);
            }
            catch (Exception ex) when (ex.Message.Contains("does not exist") || ex.Message.Contains("NoSuchBucketPolicy") || ex.Message.Contains("policy"))
            {
                isPolicyAbsentOrPrivate = true;
            }

            Assert.True(isPolicyAbsentOrPrivate, "MinIO bucket delivery-telemetry-archive must NOT have any public-read bucket policy.");

            // Anonymous HTTP Access Check: Direct unauthenticated HTTP GET must be rejected
            using var httpClient = new HttpClient();
            var anonBucketResp = await httpClient.GetAsync($"http://{MinioEndpoint}/{ArchiveBucketName}");
            Assert.True(
                anonBucketResp.StatusCode == HttpStatusCode.Forbidden ||
                anonBucketResp.StatusCode == HttpStatusCode.Unauthorized,
                $"Anonymous GET to bucket must be Forbidden/Unauthorized (got: {anonBucketResp.StatusCode})");

            // =========================================================================
            // STEP 3: Reset Test Environment
            // Stream = 0, Checkpoint = 0-0, Pending = absent
            // =========================================================================
            await redisDb.KeyDeleteAsync(TelemetryArchiveWorker.StreamKey);
            await redisDb.StringSetAsync(TelemetryArchiveWorker.CheckpointKey, "0-0");
            await redisDb.KeyDeleteAsync(TelemetryArchiveWorker.PendingBatchKey);

            var initialStreamLen = await redisDb.StreamLengthAsync(TelemetryArchiveWorker.StreamKey);
            Assert.Equal(0, initialStreamLen);

            var initialCheckpoint = await redisDb.StringGetAsync(TelemetryArchiveWorker.CheckpointKey);
            Assert.Equal("0-0", initialCheckpoint.ToString());

            var initialPending = await redisDb.StringGetAsync(TelemetryArchiveWorker.PendingBatchKey);
            Assert.True(initialPending.IsNull);

            // =========================================================================
            // STEP 4: Inject 1,000 Telemetry Entries into Redis Stream
            // =========================================================================
            const int totalEntries = 1000;
            var sourceEntries = new List<TelemetryArchivePoint>(totalEntries);
            var streamIds = new List<string>(totalEntries);
            var baseTime = DateTimeOffset.UtcNow;

            for (int i = 0; i < totalEntries; i++)
            {
                var point = new TelemetryArchivePoint
                {
                    EventId = Guid.NewGuid().ToString("D"),
                    RiderId = $"rider-{100 + (i % 25)}",
                    Timestamp = baseTime.AddMilliseconds(i * 50).ToString("o", CultureInfo.InvariantCulture),
                    Lat = Math.Round(13.750000 + (i * 0.000100), 6),
                    Lng = Math.Round(100.500000 + (i * 0.000100), 6),
                    Accuracy = Math.Round(3.5 + (i % 5) * 0.5, 2)
                };
                sourceEntries.Add(point);

                var values = new NameValueEntry[]
                {
                    new("eventId", point.EventId),
                    new("riderId", point.RiderId),
                    new("timestamp", point.Timestamp),
                    new("lat", point.Lat.ToString("F6", CultureInfo.InvariantCulture)),
                    new("lng", point.Lng.ToString("F6", CultureInfo.InvariantCulture)),
                    new("accuracy", point.Accuracy.ToString("F2", CultureInfo.InvariantCulture))
                };

                var streamId = await redisDb.StreamAddAsync(TelemetryArchiveWorker.StreamKey, values);
                streamIds.Add(streamId.ToString());
            }

            Assert.Equal(totalEntries, sourceEntries.Count);
            Assert.Equal(totalEntries, streamIds.Count);

            var populatedStreamLen = await redisDb.StreamLengthAsync(TelemetryArchiveWorker.StreamKey);
            Assert.Equal(totalEntries, populatedStreamLen);

            // =========================================================================
            // STEP 5: Worker Collect & Archive Pipeline
            // =========================================================================
            var worker = new TelemetryArchiveWorker(
                redis,
                storage,
                new NullLogger<TelemetryArchiveWorker>(),
                emptyStreamDelay: TimeSpan.FromMilliseconds(100),
                maxBatchEntries: 5000,
                maxWaitInterval: TimeSpan.FromSeconds(1));

            await worker.InitializeCheckpointAsync();
            Assert.Equal("0-0", worker.CurrentCheckpoint);

            // First collect: buffers all 1,000 entries (timer starts)
            var batch1 = await worker.CollectBatchAsync();
            Assert.Null(batch1);
            Assert.Equal(totalEntries, worker.BufferedCount);

            // Wait for 1-second timeout
            await Task.Delay(TimeSpan.FromMilliseconds(1100));

            // Second collect: timeout elapsed, flushes partial batch of 1,000
            var batch2 = await worker.CollectBatchAsync();
            Assert.NotNull(batch2);
            Assert.Equal(totalEntries, batch2.RawEntries.Count);
            Assert.Equal(totalEntries, batch2.DeduplicatedEntries.Count);
            Assert.Equal(streamIds[0], batch2.FirstStreamId);
            Assert.Equal(streamIds[^1], batch2.LastStreamId);

            // Execute the pipeline: Stage pending -> Upload MinIO -> Commit checkpoint -> Clear pending
            await worker.ProcessBatchPipelineAsync(batch2);

            // =========================================================================
            // STEP 6: Checkpoint & Staging Verification
            // =========================================================================
            var committedCheckpoint = await redisDb.StringGetAsync(TelemetryArchiveWorker.CheckpointKey);
            Assert.Equal(streamIds[^1], committedCheckpoint.ToString());
            Assert.Equal(streamIds[^1], worker.CurrentCheckpoint);

            var clearedPending = await redisDb.StringGetAsync(TelemetryArchiveWorker.PendingBatchKey);
            Assert.True(clearedPending.IsNull, "telemetry:archiver:pending_batch must be deleted after checkpoint advance");

            var streamRetainedLen = await redisDb.StreamLengthAsync(TelemetryArchiveWorker.StreamKey);
            Assert.Equal(totalEntries, streamRetainedLen); // Preserved in Redis Stream

            // =========================================================================
            // STEP 7: MinIO Object Verification & Download
            // =========================================================================
            var objectKey = batch2.GenerateObjectKey();
            bool objectExists = await storage.ObjectExistsAsync(objectKey);
            Assert.True(objectExists, $"Archive object '{objectKey}' must exist in MinIO bucket '{ArchiveBucketName}'.");

            // Anonymous HTTP Access Check to the object: must also be Forbidden
            var anonObjResp = await httpClient.GetAsync($"http://{MinioEndpoint}/{ArchiveBucketName}/{objectKey}");
            Assert.True(
                anonObjResp.StatusCode == HttpStatusCode.Forbidden ||
                anonObjResp.StatusCode == HttpStatusCode.Unauthorized,
                $"Anonymous GET to archive object must be Forbidden/Unauthorized (got: {anonObjResp.StatusCode})");

            byte[] downloadedBytes = Array.Empty<byte>();
            await storage.GetArchiveObjectAsync(objectKey, stream =>
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                downloadedBytes = ms.ToArray();
            });

            Assert.NotEmpty(downloadedBytes);

            // =========================================================================
            // STEP 8: GZip Decompress & NDJSON Parse
            // =========================================================================
            using var decompressInput = new MemoryStream(downloadedBytes);
            using var gzipStream = new GZipStream(decompressInput, CompressionMode.Decompress);
            using var reader = new StreamReader(gzipStream, Encoding.UTF8);
            var ndjsonText = await reader.ReadToEndAsync();

            Assert.EndsWith("\n", ndjsonText);
            var lines = ndjsonText.TrimEnd('\n').Split('\n');
            Assert.Equal(totalEntries, lines.Length);

            // =========================================================================
            // STEP 9: Strict 1:1 Data Equivalence Verification
            // =========================================================================
            var sourceMap = new Dictionary<string, TelemetryArchivePoint>(totalEntries);
            foreach (var p in sourceEntries)
            {
                sourceMap[p.EventId] = p;
            }

            var archivedEventIds = new HashSet<string>();

            for (int i = 0; i < lines.Length; i++)
            {
                var point = JsonSerializer.Deserialize<TelemetryArchivePoint>(lines[i]);
                Assert.NotNull(point);

                // No duplicate events in archive
                Assert.True(archivedEventIds.Add(point.EventId), $"Duplicate eventId found in archive: {point.EventId}");

                // No extra events
                Assert.True(sourceMap.TryGetValue(point.EventId, out var src), $"Archived eventId {point.EventId} not found in source");

                // Field-by-field exact comparison
                Assert.Equal(src.RiderId, point.RiderId);
                Assert.Equal(src.Timestamp, point.Timestamp);
                Assert.Equal(src.Lat, point.Lat, 6);
                Assert.Equal(src.Lng, point.Lng, 6);
                Assert.Equal(src.Accuracy, point.Accuracy, 2);
            }

            // No missing events
            Assert.Equal(totalEntries, archivedEventIds.Count);
            foreach (var srcId in sourceMap.Keys)
            {
                Assert.Contains(srcId, archivedEventIds);
            }
        }

        [Fact]
        public async Task SubStep_2_2_B_2_6_B_A_CrashBeforeUpload_FullLiveRecovery()
        {
            // =========================================================================
            // STEP 1: Connect to Real Docker Redis and Real Docker MinIO
            // =========================================================================
            var redis = await ConnectionMultiplexer.ConnectAsync(RedisConnectionString);
            var redisDb = redis.GetDatabase();

            var storage = new MinioTelemetryArchiveStorage(_configuration, new NullLogger<MinioTelemetryArchiveStorage>());
            await storage.EnsureArchiveBucketExistsAsync();

            // =========================================================================
            // STEP 2: Reset Test Environment for Case B-A
            // =========================================================================
            await redisDb.KeyDeleteAsync(TelemetryArchiveWorker.StreamKey);
            await redisDb.StringSetAsync(TelemetryArchiveWorker.CheckpointKey, "0-0");
            await redisDb.KeyDeleteAsync(TelemetryArchiveWorker.PendingBatchKey);

            // =========================================================================
            // STEP 3: Inject 250 Telemetry Entries into Redis Stream
            // =========================================================================
            const int testCount = 250;
            var sourceEntries = new List<TelemetryArchivePoint>(testCount);
            var streamIds = new List<string>(testCount);
            var baseTime = DateTimeOffset.UtcNow;

            for (int i = 0; i < testCount; i++)
            {
                var point = new TelemetryArchivePoint
                {
                    EventId = Guid.NewGuid().ToString("D"),
                    RiderId = $"rider-crash-{10 + (i % 10)}",
                    Timestamp = baseTime.AddMilliseconds(i * 100).ToString("o", CultureInfo.InvariantCulture),
                    Lat = Math.Round(13.760000 + (i * 0.000100), 6),
                    Lng = Math.Round(100.510000 + (i * 0.000100), 6),
                    Accuracy = Math.Round(4.0 + (i % 3) * 0.5, 2)
                };
                sourceEntries.Add(point);

                var values = new NameValueEntry[]
                {
                    new("eventId", point.EventId),
                    new("riderId", point.RiderId),
                    new("timestamp", point.Timestamp),
                    new("lat", point.Lat.ToString("F6", CultureInfo.InvariantCulture)),
                    new("lng", point.Lng.ToString("F6", CultureInfo.InvariantCulture)),
                    new("accuracy", point.Accuracy.ToString("F2", CultureInfo.InvariantCulture))
                };

                var streamId = await redisDb.StreamAddAsync(TelemetryArchiveWorker.StreamKey, values);
                streamIds.Add(streamId.ToString());
            }

            var firstStreamId = streamIds[0];
            var lastStreamId = streamIds[^1];

            // =========================================================================
            // STEP 4: Simulate Crash BEFORE Upload
            // Stage pending_batch in Redis, but worker process crashes before upload occurs
            // =========================================================================
            var pendingPayload = TelemetryArchiveWorker.FormatPendingBatch(firstStreamId, lastStreamId);
            await redisDb.StringSetAsync(TelemetryArchiveWorker.PendingBatchKey, pendingPayload);

            // Verify crash pre-conditions:
            // 1. pending exists
            var stagedPending = await redisDb.StringGetAsync(TelemetryArchiveWorker.PendingBatchKey);
            Assert.Equal(pendingPayload, stagedPending.ToString());

            // 2. checkpoint is strictly 0-0 (not moved)
            var checkpointBefore = await redisDb.StringGetAsync(TelemetryArchiveWorker.CheckpointKey);
            Assert.Equal("0-0", checkpointBefore.ToString());

            // 3. Object does NOT exist in MinIO yet
            var batchTime = TelemetryArchiveWorker.ExtractTimestampFromStreamId(firstStreamId, DateTimeOffset.UtcNow);
            var expectedObjectKey = TelemetryArchiveWorker.BuildObjectKey(batchTime, firstStreamId, lastStreamId);
            bool objectExistsBefore = await storage.ObjectExistsAsync(expectedObjectKey);
            Assert.False(objectExistsBefore, "Object must not exist in MinIO before recovery.");

            // =========================================================================
            // STEP 5: Restart Worker Instance & Execute Autonomous Crash Recovery
            // =========================================================================
            var restartedWorker = new TelemetryArchiveWorker(
                redis,
                storage,
                new NullLogger<TelemetryArchiveWorker>(),
                emptyStreamDelay: TimeSpan.FromMilliseconds(100),
                maxBatchEntries: 5000,
                maxWaitInterval: TimeSpan.FromSeconds(1));

            // InitializeCheckpointAsync automatically triggers RecoverPendingBatchAsync
            await restartedWorker.InitializeCheckpointAsync();

            // =========================================================================
            // STEP 6: Verify Post-Recovery State in Redis
            // =========================================================================
            // Checkpoint must advance to lastStreamId
            var committedCheckpoint = await redisDb.StringGetAsync(TelemetryArchiveWorker.CheckpointKey);
            Assert.Equal(lastStreamId, committedCheckpoint.ToString());
            Assert.Equal(lastStreamId, restartedWorker.CurrentCheckpoint);

            // Pending staging key must be cleaned up
            var clearedPending = await redisDb.StringGetAsync(TelemetryArchiveWorker.PendingBatchKey);
            Assert.True(clearedPending.IsNull, "telemetry:archiver:pending_batch must be cleared after Case B-A recovery.");

            // Stream length must be intact
            var streamLen = await redisDb.StreamLengthAsync(TelemetryArchiveWorker.StreamKey);
            Assert.Equal(testCount, streamLen);

            // =========================================================================
            // STEP 7: Verify MinIO Archive Object Created & Data Equivalence
            // =========================================================================
            bool objectExistsAfter = await storage.ObjectExistsAsync(expectedObjectKey);
            Assert.True(objectExistsAfter, $"Recovered archive object '{expectedObjectKey}' must exist in MinIO.");

            byte[] downloadedBytes = Array.Empty<byte>();
            await storage.GetArchiveObjectAsync(expectedObjectKey, stream =>
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                downloadedBytes = ms.ToArray();
            });
            Assert.NotEmpty(downloadedBytes);

            using var decompressInput = new MemoryStream(downloadedBytes);
            using var gzipStream = new GZipStream(decompressInput, CompressionMode.Decompress);
            using var reader = new StreamReader(gzipStream, Encoding.UTF8);
            var ndjsonText = await reader.ReadToEndAsync();

            var lines = ndjsonText.TrimEnd('\n').Split('\n');
            Assert.Equal(testCount, lines.Length);

            var sourceMap = new Dictionary<string, TelemetryArchivePoint>(testCount);
            foreach (var p in sourceEntries)
            {
                sourceMap[p.EventId] = p;
            }

            var archivedEventIds = new HashSet<string>();
            for (int i = 0; i < lines.Length; i++)
            {
                var point = JsonSerializer.Deserialize<TelemetryArchivePoint>(lines[i]);
                Assert.NotNull(point);
                Assert.True(archivedEventIds.Add(point.EventId), $"Duplicate eventId found in recovered archive: {point.EventId}");
                Assert.True(sourceMap.TryGetValue(point.EventId, out var src), $"Archived eventId {point.EventId} not in source");
                Assert.Equal(src.RiderId, point.RiderId);
                Assert.Equal(src.Timestamp, point.Timestamp);
                Assert.Equal(src.Lat, point.Lat, 6);
                Assert.Equal(src.Lng, point.Lng, 6);
                Assert.Equal(src.Accuracy, point.Accuracy, 2);
            }
            Assert.Equal(testCount, archivedEventIds.Count);

            // =========================================================================
            // STEP 8: Idempotency on Second Restart
            // Verify that restarting worker again does NOT re-archive or modify checkpoint
            // =========================================================================
            var secondRestartWorker = new TelemetryArchiveWorker(
                redis,
                storage,
                new NullLogger<TelemetryArchiveWorker>());

            await secondRestartWorker.InitializeCheckpointAsync();
            Assert.Equal(lastStreamId, secondRestartWorker.CurrentCheckpoint);
            Assert.False(secondRestartWorker.IsHalted);
        }

        [Fact]
        public async Task SubStep_2_2_B_2_6_B_B_CrashAfterUploadBeforeCheckpoint_FullLiveRecovery()
        {
            // =========================================================================
            // STEP 1: Connect to Real Docker Redis and Real Docker MinIO
            // =========================================================================
            var redis = await ConnectionMultiplexer.ConnectAsync(RedisConnectionString);
            var redisDb = redis.GetDatabase();

            var storage = new MinioTelemetryArchiveStorage(_configuration, new NullLogger<MinioTelemetryArchiveStorage>());
            await storage.EnsureArchiveBucketExistsAsync();

            // =========================================================================
            // STEP 2: Reset Test Environment for Case B-B
            // =========================================================================
            await redisDb.KeyDeleteAsync(TelemetryArchiveWorker.StreamKey);
            await redisDb.StringSetAsync(TelemetryArchiveWorker.CheckpointKey, "0-0");
            await redisDb.KeyDeleteAsync(TelemetryArchiveWorker.PendingBatchKey);

            // =========================================================================
            // STEP 3: Inject 250 Telemetry Entries into Redis Stream
            // =========================================================================
            const int testCount = 250;
            var sourceEntries = new List<TelemetryArchivePoint>(testCount);
            var streamIds = new List<string>(testCount);
            var baseTime = DateTimeOffset.UtcNow;

            for (int i = 0; i < testCount; i++)
            {
                var point = new TelemetryArchivePoint
                {
                    EventId = Guid.NewGuid().ToString("D"),
                    RiderId = $"rider-bb-{10 + (i % 10)}",
                    Timestamp = baseTime.AddMilliseconds(i * 100).ToString("o", CultureInfo.InvariantCulture),
                    Lat = Math.Round(13.770000 + (i * 0.000100), 6),
                    Lng = Math.Round(100.520000 + (i * 0.000100), 6),
                    Accuracy = Math.Round(4.5 + (i % 3) * 0.5, 2)
                };
                sourceEntries.Add(point);

                var values = new NameValueEntry[]
                {
                    new("eventId", point.EventId),
                    new("riderId", point.RiderId),
                    new("timestamp", point.Timestamp),
                    new("lat", point.Lat.ToString("F6", CultureInfo.InvariantCulture)),
                    new("lng", point.Lng.ToString("F6", CultureInfo.InvariantCulture)),
                    new("accuracy", point.Accuracy.ToString("F2", CultureInfo.InvariantCulture))
                };

                var streamId = await redisDb.StreamAddAsync(TelemetryArchiveWorker.StreamKey, values);
                streamIds.Add(streamId.ToString());
            }

            var firstStreamId = streamIds[0];
            var lastStreamId = streamIds[^1];

            // =========================================================================
            // STEP 4: Collect Batch, Stage Pending, Upload to MinIO, then CRASH BEFORE Checkpoint
            // =========================================================================
            var workerPreCrash = new TelemetryArchiveWorker(
                redis,
                storage,
                new NullLogger<TelemetryArchiveWorker>(),
                emptyStreamDelay: TimeSpan.FromMilliseconds(100),
                maxBatchEntries: 5000,
                maxWaitInterval: TimeSpan.FromSeconds(1));

            // Buffer entries
            var initialCollect = await workerPreCrash.CollectBatchAsync();
            Assert.Null(initialCollect);
            Assert.Equal(testCount, workerPreCrash.BufferedCount);

            // Wait for 1-second timeout to trigger partial flush
            await Task.Delay(TimeSpan.FromMilliseconds(1100));
            var batch = await workerPreCrash.CollectBatchAsync();
            Assert.NotNull(batch);
            Assert.Equal(testCount, batch.RawEntries.Count);
            Assert.Equal(firstStreamId, batch.FirstStreamId);
            Assert.Equal(lastStreamId, batch.LastStreamId);

            // Stage pending_batch in Redis (Pipeline Step 1)
            var pendingPayload = batch.ToPendingBatchPayload();
            await redisDb.StringSetAsync(TelemetryArchiveWorker.PendingBatchKey, pendingPayload);

            // Upload object to MinIO cold storage (Pipeline Step 2)
            var objectKey = await workerPreCrash.UploadBatchAsync(batch);

            // CRASH OCCURS HERE:
            // Worker process terminates immediately before calling:
            // - StringSetAsync(CheckpointKey, lastStreamId)
            // - KeyDeleteAsync(PendingBatchKey)

            // =========================================================================
            // STEP 5: Verify Pre-Crash Invariant State (Content validity check BEFORE restart)
            // =========================================================================
            // 1. Pending exists
            var stagedPending = await redisDb.StringGetAsync(TelemetryArchiveWorker.PendingBatchKey);
            Assert.Equal(pendingPayload, stagedPending.ToString());

            // 2. Checkpoint is strictly < lastStreamId ("0-0")
            var checkpointBefore = await redisDb.StringGetAsync(TelemetryArchiveWorker.CheckpointKey);
            Assert.Equal("0-0", checkpointBefore.ToString());

            // 3. Object exists in MinIO
            bool objectExistsBefore = await storage.ObjectExistsAsync(objectKey);
            Assert.True(objectExistsBefore, $"Object '{objectKey}' must already exist in MinIO before recovery.");

            // 4. Download and verify content of the pre-existing object
            byte[] preCrashBytes = Array.Empty<byte>();
            await storage.GetArchiveObjectAsync(objectKey, stream =>
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                preCrashBytes = ms.ToArray();
            });
            Assert.NotEmpty(preCrashBytes);

            using (var decompressInput = new MemoryStream(preCrashBytes))
            using (var gzipStream = new GZipStream(decompressInput, CompressionMode.Decompress))
            using (var reader = new StreamReader(gzipStream, Encoding.UTF8))
            {
                var ndjsonText = await reader.ReadToEndAsync();
                var lines = ndjsonText.TrimEnd('\n').Split('\n');
                Assert.Equal(testCount, lines.Length);

                var sourceMap = new Dictionary<string, TelemetryArchivePoint>(testCount);
                foreach (var p in sourceEntries) sourceMap[p.EventId] = p;

                var preArchivedEventIds = new HashSet<string>();
                for (int i = 0; i < lines.Length; i++)
                {
                    var point = JsonSerializer.Deserialize<TelemetryArchivePoint>(lines[i]);
                    Assert.NotNull(point);
                    Assert.True(preArchivedEventIds.Add(point.EventId), $"Duplicate eventId found: {point.EventId}");
                    Assert.True(sourceMap.TryGetValue(point.EventId, out var src), $"Archived eventId {point.EventId} not in source");
                    Assert.Equal(src.RiderId, point.RiderId);
                    Assert.Equal(src.Timestamp, point.Timestamp);
                    Assert.Equal(src.Lat, point.Lat, 6);
                    Assert.Equal(src.Lng, point.Lng, 6);
                    Assert.Equal(src.Accuracy, point.Accuracy, 2);
                }
                Assert.Equal(testCount, preArchivedEventIds.Count);
            }

            // =========================================================================
            // STEP 6: Worker Restart with Tracking Storage (Assert NO additional uploads)
            // =========================================================================
            var trackingStorage = new TrackingTelemetryArchiveStorage(storage);
            var restartedWorker = new TelemetryArchiveWorker(
                redis,
                trackingStorage,
                new NullLogger<TelemetryArchiveWorker>(),
                emptyStreamDelay: TimeSpan.FromMilliseconds(100),
                maxBatchEntries: 5000,
                maxWaitInterval: TimeSpan.FromSeconds(1));

            // Boot restart worker -> triggers RecoverPendingBatchAsync
            await restartedWorker.InitializeCheckpointAsync();

            // =========================================================================
            // STEP 7: Verify Post-Recovery State (Case B)
            // =========================================================================
            // 1. Checkpoint must advance to lastStreamId
            var committedCheckpoint = await redisDb.StringGetAsync(TelemetryArchiveWorker.CheckpointKey);
            Assert.Equal(lastStreamId, committedCheckpoint.ToString());
            Assert.Equal(lastStreamId, restartedWorker.CurrentCheckpoint);

            // 2. Pending batch must be deleted
            var clearedPending = await redisDb.StringGetAsync(TelemetryArchiveWorker.PendingBatchKey);
            Assert.True(clearedPending.IsNull, "telemetry:archiver:pending_batch must be deleted after Case B-B recovery.");

            // 3. PutArchiveObjectAsync MUST NOT have been called again (zero additional upload)
            Assert.Equal(0, trackingStorage.PutObjectCallCount);

            // 4. Redis Stream length intact
            var streamLen = await redisDb.StreamLengthAsync(TelemetryArchiveWorker.StreamKey);
            Assert.Equal(testCount, streamLen);

            // 5. MinIO Object content remains 100% valid after recovery
            byte[] postRecoveryBytes = Array.Empty<byte>();
            await storage.GetArchiveObjectAsync(objectKey, stream =>
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                postRecoveryBytes = ms.ToArray();
            });
            Assert.NotEmpty(postRecoveryBytes);
            Assert.Equal(preCrashBytes.Length, postRecoveryBytes.Length);

            // =========================================================================
            // STEP 8: Idempotency on Second Worker Restart
            // =========================================================================
            var secondRestartWorker = new TelemetryArchiveWorker(
                redis,
                trackingStorage,
                new NullLogger<TelemetryArchiveWorker>());

            await secondRestartWorker.InitializeCheckpointAsync();
            Assert.Equal(lastStreamId, secondRestartWorker.CurrentCheckpoint);
            Assert.Equal(0, trackingStorage.PutObjectCallCount);
            Assert.False(secondRestartWorker.IsHalted);
        }

        [Fact]
        public async Task SubStep_2_2_B_2_6_B_C_CrashAfterCheckpointBeforeDeletePending_FullLiveRecovery()
        {
            // =========================================================================
            // STEP 1: Connect to Real Docker Redis and Real Docker MinIO
            // =========================================================================
            var redis = await ConnectionMultiplexer.ConnectAsync(RedisConnectionString);
            var redisDb = redis.GetDatabase();

            var storage = new MinioTelemetryArchiveStorage(_configuration, new NullLogger<MinioTelemetryArchiveStorage>());
            await storage.EnsureArchiveBucketExistsAsync();

            // =========================================================================
            // STEP 2: Reset Test Environment for Case B-C
            // =========================================================================
            await redisDb.KeyDeleteAsync(TelemetryArchiveWorker.StreamKey);
            await redisDb.StringSetAsync(TelemetryArchiveWorker.CheckpointKey, "0-0");
            await redisDb.KeyDeleteAsync(TelemetryArchiveWorker.PendingBatchKey);

            // =========================================================================
            // STEP 3: Inject 250 Telemetry Entries into Redis Stream
            // =========================================================================
            const int testCount = 250;
            var sourceEntries = new List<TelemetryArchivePoint>(testCount);
            var streamIds = new List<string>(testCount);
            var baseTime = DateTimeOffset.UtcNow;

            for (int i = 0; i < testCount; i++)
            {
                var point = new TelemetryArchivePoint
                {
                    EventId = Guid.NewGuid().ToString("D"),
                    RiderId = $"rider-bc-{10 + (i % 10)}",
                    Timestamp = baseTime.AddMilliseconds(i * 100).ToString("o", CultureInfo.InvariantCulture),
                    Lat = Math.Round(13.780000 + (i * 0.000100), 6),
                    Lng = Math.Round(100.530000 + (i * 0.000100), 6),
                    Accuracy = Math.Round(5.0 + (i % 3) * 0.5, 2)
                };
                sourceEntries.Add(point);

                var values = new NameValueEntry[]
                {
                    new("eventId", point.EventId),
                    new("riderId", point.RiderId),
                    new("timestamp", point.Timestamp),
                    new("lat", point.Lat.ToString("F6", CultureInfo.InvariantCulture)),
                    new("lng", point.Lng.ToString("F6", CultureInfo.InvariantCulture)),
                    new("accuracy", point.Accuracy.ToString("F2", CultureInfo.InvariantCulture))
                };

                var streamId = await redisDb.StreamAddAsync(TelemetryArchiveWorker.StreamKey, values);
                streamIds.Add(streamId.ToString());
            }

            var firstStreamId = streamIds[0];
            var lastStreamId = streamIds[^1];

            // =========================================================================
            // STEP 4: Collect Batch, Stage Pending, Upload to MinIO, Commit Checkpoint,
            // then CRASH BEFORE Deleting Pending Batch (Case B-C Simulation)
            // =========================================================================
            var workerPreCrash = new TelemetryArchiveWorker(
                redis,
                storage,
                new NullLogger<TelemetryArchiveWorker>(),
                emptyStreamDelay: TimeSpan.FromMilliseconds(100),
                maxBatchEntries: 5000,
                maxWaitInterval: TimeSpan.FromSeconds(1));

            // Buffer entries
            var initialCollect = await workerPreCrash.CollectBatchAsync();
            Assert.Null(initialCollect);
            Assert.Equal(testCount, workerPreCrash.BufferedCount);

            // Wait for 1-second timeout to trigger partial flush
            await Task.Delay(TimeSpan.FromMilliseconds(1100));
            var batch = await workerPreCrash.CollectBatchAsync();
            Assert.NotNull(batch);
            Assert.Equal(testCount, batch.RawEntries.Count);
            Assert.Equal(firstStreamId, batch.FirstStreamId);
            Assert.Equal(lastStreamId, batch.LastStreamId);

            // Stage pending_batch in Redis (Pipeline Step 1)
            var pendingPayload = batch.ToPendingBatchPayload();
            await redisDb.StringSetAsync(TelemetryArchiveWorker.PendingBatchKey, pendingPayload);

            // Upload object to MinIO cold storage (Pipeline Step 2)
            var objectKey = await workerPreCrash.UploadBatchAsync(batch);

            // Commit Checkpoint in Redis (Pipeline Step 3)
            await redisDb.StringSetAsync(TelemetryArchiveWorker.CheckpointKey, lastStreamId);

            // CRASH OCCURS HERE:
            // Worker process terminates immediately before calling:
            // - KeyDeleteAsync(PendingBatchKey) (Pipeline Step 4)

            // =========================================================================
            // STEP 5: Verify Pre-Recovery Invariant State
            // =========================================================================
            // 1. Pending staging key still exists in Redis
            var stagedPending = await redisDb.StringGetAsync(TelemetryArchiveWorker.PendingBatchKey);
            Assert.Equal(pendingPayload, stagedPending.ToString());

            // 2. Checkpoint is ALREADY committed to lastStreamId
            var checkpointBefore = await redisDb.StringGetAsync(TelemetryArchiveWorker.CheckpointKey);
            Assert.Equal(lastStreamId, checkpointBefore.ToString());

            // 3. Object exists in MinIO
            bool objectExistsBefore = await storage.ObjectExistsAsync(objectKey);
            Assert.True(objectExistsBefore, $"Object '{objectKey}' must already exist in MinIO before recovery.");

            // 4. Download and verify content of the object in MinIO
            byte[] preCrashBytes = Array.Empty<byte>();
            await storage.GetArchiveObjectAsync(objectKey, stream =>
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                preCrashBytes = ms.ToArray();
            });
            Assert.NotEmpty(preCrashBytes);

            using (var decompressInput = new MemoryStream(preCrashBytes))
            using (var gzipStream = new GZipStream(decompressInput, CompressionMode.Decompress))
            using (var reader = new StreamReader(gzipStream, Encoding.UTF8))
            {
                var ndjsonText = await reader.ReadToEndAsync();
                var lines = ndjsonText.TrimEnd('\n').Split('\n');
                Assert.Equal(testCount, lines.Length);

                var sourceMap = new Dictionary<string, TelemetryArchivePoint>(testCount);
                foreach (var p in sourceEntries) sourceMap[p.EventId] = p;

                var preArchivedEventIds = new HashSet<string>();
                for (int i = 0; i < lines.Length; i++)
                {
                    var point = JsonSerializer.Deserialize<TelemetryArchivePoint>(lines[i]);
                    Assert.NotNull(point);
                    Assert.True(preArchivedEventIds.Add(point.EventId), $"Duplicate eventId found: {point.EventId}");
                    Assert.True(sourceMap.TryGetValue(point.EventId, out var src), $"Archived eventId {point.EventId} not in source");
                    Assert.Equal(src.RiderId, point.RiderId);
                    Assert.Equal(src.Timestamp, point.Timestamp);
                    Assert.Equal(src.Lat, point.Lat, 6);
                    Assert.Equal(src.Lng, point.Lng, 6);
                    Assert.Equal(src.Accuracy, point.Accuracy, 2);
                }
                Assert.Equal(testCount, preArchivedEventIds.Count);
            }

            // =========================================================================
            // STEP 6: Worker Restart with Tracking Storage (Case B-C Recovery)
            // =========================================================================
            var trackingStorage = new TrackingTelemetryArchiveStorage(storage);
            var restartedWorker = new TelemetryArchiveWorker(
                redis,
                trackingStorage,
                new NullLogger<TelemetryArchiveWorker>(),
                emptyStreamDelay: TimeSpan.FromMilliseconds(100),
                maxBatchEntries: 5000,
                maxWaitInterval: TimeSpan.FromSeconds(1));

            // Boot restart worker -> triggers RecoverPendingBatchAsync
            await restartedWorker.InitializeCheckpointAsync();

            // =========================================================================
            // STEP 7: Verify Post-Recovery State (Case C: Checkpoint >= lastStreamId)
            // =========================================================================
            // 1. Checkpoint must remain at lastStreamId (NOT rolled back or corrupted)
            var committedCheckpoint = await redisDb.StringGetAsync(TelemetryArchiveWorker.CheckpointKey);
            Assert.Equal(lastStreamId, committedCheckpoint.ToString());
            Assert.Equal(lastStreamId, restartedWorker.CurrentCheckpoint);

            // 2. Pending staging key must be cleaned up
            var clearedPending = await redisDb.StringGetAsync(TelemetryArchiveWorker.PendingBatchKey);
            Assert.True(clearedPending.IsNull, "telemetry:archiver:pending_batch must be deleted after Case B-C recovery.");

            // 3. PutArchiveObjectAsync MUST NOT have been called (zero additional uploads)
            Assert.Equal(0, trackingStorage.PutObjectCallCount);

            // 4. Redis Stream length intact
            var streamLen = await redisDb.StreamLengthAsync(TelemetryArchiveWorker.StreamKey);
            Assert.Equal(testCount, streamLen);

            // 5. MinIO Object content remains identical and uncorrupted
            byte[] postRecoveryBytes = Array.Empty<byte>();
            await storage.GetArchiveObjectAsync(objectKey, stream =>
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                postRecoveryBytes = ms.ToArray();
            });
            Assert.NotEmpty(postRecoveryBytes);
            Assert.Equal(preCrashBytes.Length, postRecoveryBytes.Length);

            // =========================================================================
            // STEP 8: Idempotency on Second Worker Restart
            // =========================================================================
            var secondRestartWorker = new TelemetryArchiveWorker(
                redis,
                trackingStorage,
                new NullLogger<TelemetryArchiveWorker>());

            await secondRestartWorker.InitializeCheckpointAsync();
            Assert.Equal(lastStreamId, secondRestartWorker.CurrentCheckpoint);
            Assert.Equal(0, trackingStorage.PutObjectCallCount);
            Assert.False(secondRestartWorker.IsHalted);
        }

        [Fact]
        public async Task SubStep_2_2_B_2_6_C_MinioOutageAndRecovery_LiveRecovery()
        {
            // =========================================================================
            // STEP 1: Connect to Real Docker Redis and Real Docker MinIO
            // =========================================================================
            var redis = await ConnectionMultiplexer.ConnectAsync(RedisConnectionString);
            var redisDb = redis.GetDatabase();

            var storage = new MinioTelemetryArchiveStorage(_configuration, new NullLogger<MinioTelemetryArchiveStorage>());
            await storage.EnsureArchiveBucketExistsAsync();

            // =========================================================================
            // STEP 2: Reset Test Environment for Case C
            // =========================================================================
            await redisDb.KeyDeleteAsync(TelemetryArchiveWorker.StreamKey);
            await redisDb.StringSetAsync(TelemetryArchiveWorker.CheckpointKey, "0-0");
            await redisDb.KeyDeleteAsync(TelemetryArchiveWorker.PendingBatchKey);

            // =========================================================================
            // STEP 3: Inject 250 Telemetry Entries into Redis Stream
            // =========================================================================
            const int testCount = 250;
            var sourceEntries = new List<TelemetryArchivePoint>(testCount);
            var streamIds = new List<string>(testCount);
            var baseTime = DateTimeOffset.UtcNow;

            for (int i = 0; i < testCount; i++)
            {
                var point = new TelemetryArchivePoint
                {
                    EventId = Guid.NewGuid().ToString("D"),
                    RiderId = $"rider-c-{10 + (i % 10)}",
                    Timestamp = baseTime.AddMilliseconds(i * 100).ToString("o", CultureInfo.InvariantCulture),
                    Lat = Math.Round(13.790000 + (i * 0.000100), 6),
                    Lng = Math.Round(100.540000 + (i * 0.000100), 6),
                    Accuracy = Math.Round(3.8 + (i % 3) * 0.5, 2)
                };
                sourceEntries.Add(point);

                var values = new NameValueEntry[]
                {
                    new("eventId", point.EventId),
                    new("riderId", point.RiderId),
                    new("timestamp", point.Timestamp),
                    new("lat", point.Lat.ToString("F6", CultureInfo.InvariantCulture)),
                    new("lng", point.Lng.ToString("F6", CultureInfo.InvariantCulture)),
                    new("accuracy", point.Accuracy.ToString("F2", CultureInfo.InvariantCulture))
                };

                var streamId = await redisDb.StreamAddAsync(TelemetryArchiveWorker.StreamKey, values);
                streamIds.Add(streamId.ToString());
            }

            var firstStreamId = streamIds[0];
            var lastStreamId = streamIds[^1];

            // =========================================================================
            // STEP 4: Worker Collects Batch & Stages Pending Staging Key
            // =========================================================================
            var worker = new TelemetryArchiveWorker(
                redis,
                storage,
                new NullLogger<TelemetryArchiveWorker>(),
                emptyStreamDelay: TimeSpan.FromMilliseconds(100),
                maxBatchEntries: 5000,
                maxWaitInterval: TimeSpan.FromSeconds(1));

            var initialCollect = await worker.CollectBatchAsync();
            Assert.Null(initialCollect);
            Assert.Equal(testCount, worker.BufferedCount);

            await Task.Delay(TimeSpan.FromMilliseconds(1100));
            var batch = await worker.CollectBatchAsync();
            Assert.NotNull(batch);
            Assert.Equal(testCount, batch.RawEntries.Count);
            Assert.Equal(firstStreamId, batch.FirstStreamId);
            Assert.Equal(lastStreamId, batch.LastStreamId);

            // =========================================================================
            // STEP 5: Trigger MinIO Outage & Execute Pipeline (Upload Must Fail)
            // =========================================================================
            try
            {
                // Stop MinIO container to simulate sudden infrastructure outage
                RunDockerCommand("stop delivery-v2-minio");

                // Execute batch pipeline during outage
                // Step 1: Stages pending_batch
                // Step 2: Calls UploadBatchAsync -> FAILS because MinIO is DOWN!
                await Assert.ThrowsAnyAsync<Exception>(async () =>
                {
                    await worker.ProcessBatchPipelineAsync(batch);
                });

                // =========================================================================
                // STEP 6: Invariant Verification During Outage
                // =========================================================================
                // 1. Checkpoint MUST NOT change (remains strictly "0-0")
                var checkpointDuringOutage = await redisDb.StringGetAsync(TelemetryArchiveWorker.CheckpointKey);
                Assert.Equal("0-0", checkpointDuringOutage.ToString());
                Assert.Equal("0-0", worker.CurrentCheckpoint);

                // 2. Pending batch MUST remain in Redis
                var pendingDuringOutage = await redisDb.StringGetAsync(TelemetryArchiveWorker.PendingBatchKey);
                var expectedPending = batch.ToPendingBatchPayload();
                Assert.Equal(expectedPending, pendingDuringOutage.ToString());

                // 3. Worker process is NOT halted or corrupted
                Assert.False(worker.IsHalted);

                // 4. Redis Stream length intact
                var streamLenDuringOutage = await redisDb.StreamLengthAsync(TelemetryArchiveWorker.StreamKey);
                Assert.Equal(testCount, streamLenDuringOutage);
            }
            finally
            {
                // Guarantee MinIO container is restored
                RunDockerCommand("start delivery-v2-minio");
            }

            // =========================================================================
            // STEP 7: MinIO UP - Await Container Health & Readiness
            // =========================================================================
            bool minioReady = false;
            for (int i = 0; i < 30; i++)
            {
                try
                {
                    await storage.EnsureArchiveBucketExistsAsync();
                    minioReady = true;
                    break;
                }
                catch
                {
                    await Task.Delay(500);
                }
            }
            Assert.True(minioReady, "MinIO container must recover and become ready after restart.");

            // =========================================================================
            // STEP 8: Retry Processing Batch & Complete Pipeline
            // =========================================================================
            // Worker retries processing the batch after MinIO recovers
            await worker.ProcessBatchPipelineAsync(batch);

            // =========================================================================
            // STEP 9: Verify Post-Recovery State
            // =========================================================================
            // 1. Checkpoint must advance to lastStreamId
            var committedCheckpoint = await redisDb.StringGetAsync(TelemetryArchiveWorker.CheckpointKey);
            Assert.Equal(lastStreamId, committedCheckpoint.ToString());
            Assert.Equal(lastStreamId, worker.CurrentCheckpoint);

            // 2. Pending batch must be deleted
            var clearedPending = await redisDb.StringGetAsync(TelemetryArchiveWorker.PendingBatchKey);
            Assert.True(clearedPending.IsNull, "telemetry:archiver:pending_batch must be deleted after recovery upload succeeds.");

            // 3. Redis Stream length intact
            var streamLenAfter = await redisDb.StreamLengthAsync(TelemetryArchiveWorker.StreamKey);
            Assert.Equal(testCount, streamLenAfter);

            // 4. Download and verify content of the newly created archive object
            var objectKey = batch.GenerateObjectKey();
            bool objectExists = await storage.ObjectExistsAsync(objectKey);
            Assert.True(objectExists, $"Archive object '{objectKey}' must exist in MinIO after recovery.");

            byte[] downloadedBytes = Array.Empty<byte>();
            await storage.GetArchiveObjectAsync(objectKey, stream =>
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                downloadedBytes = ms.ToArray();
            });
            Assert.NotEmpty(downloadedBytes);

            using (var decompressInput = new MemoryStream(downloadedBytes))
            using (var gzipStream = new GZipStream(decompressInput, CompressionMode.Decompress))
            using (var reader = new StreamReader(gzipStream, Encoding.UTF8))
            {
                var ndjsonText = await reader.ReadToEndAsync();
                var lines = ndjsonText.TrimEnd('\n').Split('\n');
                Assert.Equal(testCount, lines.Length);

                var sourceMap = new Dictionary<string, TelemetryArchivePoint>(testCount);
                foreach (var p in sourceEntries) sourceMap[p.EventId] = p;

                var archivedEventIds = new HashSet<string>();
                for (int i = 0; i < lines.Length; i++)
                {
                    var point = JsonSerializer.Deserialize<TelemetryArchivePoint>(lines[i]);
                    Assert.NotNull(point);
                    Assert.True(archivedEventIds.Add(point.EventId), $"Duplicate eventId found: {point.EventId}");
                    Assert.True(sourceMap.TryGetValue(point.EventId, out var src), $"Archived eventId {point.EventId} not in source");
                    Assert.Equal(src.RiderId, point.RiderId);
                    Assert.Equal(src.Timestamp, point.Timestamp);
                    Assert.Equal(src.Lat, point.Lat, 6);
                    Assert.Equal(src.Lng, point.Lng, 6);
                    Assert.Equal(src.Accuracy, point.Accuracy, 2);
                }
                Assert.Equal(testCount, archivedEventIds.Count);
            }

            // =========================================================================
            // STEP 10: Idempotency on Worker Restart After Outage Recovery
            // =========================================================================
            var restartedWorker = new TelemetryArchiveWorker(
                redis,
                storage,
                new NullLogger<TelemetryArchiveWorker>());

            await restartedWorker.InitializeCheckpointAsync();
            Assert.Equal(lastStreamId, restartedWorker.CurrentCheckpoint);
            Assert.False(restartedWorker.IsHalted);
        }

        private static void RunDockerCommand(string args)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            process.WaitForExit(15000);
        }

        [Fact]
        public async Task SubStep_2_2_B_2_6_D_TrimGapSafeHalt_LiveVerification()
        {
            // =========================================================================
            // STEP 1: Connect to Real Docker Redis and Real Docker MinIO
            // =========================================================================
            var redis = await ConnectionMultiplexer.ConnectAsync(RedisConnectionString);
            var redisDb = redis.GetDatabase();

            var storage = new MinioTelemetryArchiveStorage(_configuration, new NullLogger<MinioTelemetryArchiveStorage>());
            await storage.EnsureArchiveBucketExistsAsync();

            // =========================================================================
            // STEP 2: Reset Test Environment for Case D
            // =========================================================================
            await redisDb.KeyDeleteAsync(TelemetryArchiveWorker.StreamKey);
            await redisDb.StringSetAsync(TelemetryArchiveWorker.CheckpointKey, "0-0");
            await redisDb.KeyDeleteAsync(TelemetryArchiveWorker.PendingBatchKey);

            // =========================================================================
            // STEP 3: Inject 250 Telemetry Entries into Redis Stream
            // =========================================================================
            const int testCount = 250;
            var streamIds = new List<string>(testCount);
            var baseTime = DateTimeOffset.UtcNow;

            for (int i = 0; i < testCount; i++)
            {
                var values = new NameValueEntry[]
                {
                    new("eventId", Guid.NewGuid().ToString("D")),
                    new("riderId", $"rider-d-{10 + (i % 10)}"),
                    new("timestamp", baseTime.AddMilliseconds(i * 100).ToString("o", CultureInfo.InvariantCulture)),
                    new("lat", (13.800000 + (i * 0.000100)).ToString("F6", CultureInfo.InvariantCulture)),
                    new("lng", (100.550000 + (i * 0.000100)).ToString("F6", CultureInfo.InvariantCulture)),
                    new("accuracy", "4.00")
                };

                var streamId = await redisDb.StreamAddAsync(TelemetryArchiveWorker.StreamKey, values);
                streamIds.Add(streamId.ToString());
            }

            var firstStreamId = streamIds[0];
            var lastStreamId = streamIds[^1];

            // =========================================================================
            // STEP 4: Stage Pending Batch in Redis (firstStreamId | lastStreamId)
            // =========================================================================
            var pendingPayload = TelemetryArchiveWorker.FormatPendingBatch(firstStreamId, lastStreamId);
            await redisDb.StringSetAsync(TelemetryArchiveWorker.PendingBatchKey, pendingPayload);

            var stagedPending = await redisDb.StringGetAsync(TelemetryArchiveWorker.PendingBatchKey);
            Assert.Equal(pendingPayload, stagedPending.ToString());

            // =========================================================================
            // STEP 5: Deliberately Prune/Trim Head of Stream (Simulate Redis Trimming Gap)
            // Delete firstStreamId from Redis Stream using XDEL
            // =========================================================================
            var xdelCount = await redisDb.StreamDeleteAsync(TelemetryArchiveWorker.StreamKey, new RedisValue[] { firstStreamId });
            Assert.Equal(1, xdelCount);

            // Verify firstStreamId is truly gone
            var checkPruned = await redisDb.StreamRangeAsync(TelemetryArchiveWorker.StreamKey, firstStreamId, firstStreamId);
            Assert.Empty(checkPruned);

            // Verify remaining entries in stream are still intact
            var remainingStreamLen = await redisDb.StreamLengthAsync(TelemetryArchiveWorker.StreamKey);
            Assert.Equal(testCount - 1, remainingStreamLen);

            // Ensure MinIO object does NOT exist
            var batchTime = TelemetryArchiveWorker.ExtractTimestampFromStreamId(firstStreamId, DateTimeOffset.UtcNow);
            var objectKey = TelemetryArchiveWorker.BuildObjectKey(batchTime, firstStreamId, lastStreamId);
            Assert.False(await storage.ObjectExistsAsync(objectKey));

            // =========================================================================
            // STEP 6: Worker Bootstraps & Triggers Recovery (Must Enter Safe Halt)
            // =========================================================================
            var memoryLogger = new TestMemoryLogger<TelemetryArchiveWorker>();
            var worker = new TelemetryArchiveWorker(
                redis,
                storage,
                memoryLogger,
                emptyStreamDelay: TimeSpan.FromMilliseconds(100),
                maxBatchEntries: 5000,
                maxWaitInterval: TimeSpan.FromSeconds(1));

            // Must throw TelemetryStreamTrimGapException
            var ex = await Assert.ThrowsAsync<TelemetryStreamTrimGapException>(async () =>
            {
                await worker.InitializeCheckpointAsync();
            });

            Assert.Contains(firstStreamId, ex.Message);

            // =========================================================================
            // STEP 7: Verify Strict Safety Boundary Invariants
            // =========================================================================
            // 1. Worker entered Safe Halt state
            Assert.True(worker.IsHalted, "Worker must enter Safe Halt state (IsHalted == true) upon detecting trim gap.");

            // 2. CRITICAL log was emitted
            Assert.Contains(memoryLogger.Logs, l =>
                l.Level == LogLevel.Critical &&
                l.Message.Contains("CRITICAL ARCHITECTURE VIOLATION") &&
                l.Message.Contains("trim-gap"));

            // 3. Checkpoint in Redis did NOT advance (remains strictly "0-0")
            var checkpointAfter = await redisDb.StringGetAsync(TelemetryArchiveWorker.CheckpointKey);
            Assert.Equal("0-0", checkpointAfter.ToString());
            Assert.Equal("0-0", worker.CurrentCheckpoint);

            // 4. Pending batch key was NOT deleted (remains for operator intervention)
            var pendingAfter = await redisDb.StringGetAsync(TelemetryArchiveWorker.PendingBatchKey);
            Assert.Equal(pendingPayload, pendingAfter.ToString());

            // 5. MinIO Object was NOT created (no partial or skipped upload)
            Assert.False(await storage.ObjectExistsAsync(objectKey));

            // 6. Remaining Stream entries were NOT touched or skipped by Worker
            var finalStreamLen = await redisDb.StreamLengthAsync(TelemetryArchiveWorker.StreamKey);
            Assert.Equal(testCount - 1, finalStreamLen);
        }

        private sealed class TestMemoryLogger<T> : ILogger<T>
        {
            public List<(LogLevel Level, string Message)> Logs { get; } = new();

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                Logs.Add((logLevel, formatter(state, exception)));
            }
        }

        [Fact]
        public async Task SubStep_2_2_B_2_6_E_FinalDataEquivalence_MultiBatchReconciliation()
        {
            // =========================================================================
            // STEP 1: Connect to Real Docker Redis and Real Docker MinIO
            // =========================================================================
            var redis = await ConnectionMultiplexer.ConnectAsync(RedisConnectionString);
            var redisDb = redis.GetDatabase();

            var storage = new MinioTelemetryArchiveStorage(_configuration, new NullLogger<MinioTelemetryArchiveStorage>());
            await storage.EnsureArchiveBucketExistsAsync();

            // =========================================================================
            // STEP 2: Reset Test Environment (Clean Slate)
            // =========================================================================
            var server = redis.GetServer(redis.GetEndPoints()[0]);
            foreach (var key in server.Keys(pattern: "telemetry:archiver:*"))
            {
                await redisDb.KeyDeleteAsync(key);
            }
            await redisDb.KeyDeleteAsync(TelemetryArchiveWorker.StreamKey);
            await redisDb.StringSetAsync(TelemetryArchiveWorker.CheckpointKey, "0-0");
            await redisDb.KeyDeleteAsync(TelemetryArchiveWorker.PendingBatchKey);

            // =========================================================================
            // STEP 3: Inject Multi-Batch Stream with Deliberate In-Batch & Cross-Batch Duplicates
            // Batch 1: 300 entries (297 unique + 3 in-batch duplicates)
            // Batch 2: 300 entries (296 unique + 2 cross-batch duplicates from B1 + 2 in-batch duplicates)
            // Batch 3: 150 entries (149 unique + 1 cross-batch duplicate from B2)
            // Total Raw Stream Entries = 750
            // Total Unique Events = 742 (8 duplicates injected)
            // =========================================================================
            const int batch1Count = 300;
            const int batch2Count = 300;
            const int batch3Count = 150;
            const int totalRawEntries = batch1Count + batch2Count + batch3Count; // 750

            var sourceMap = new Dictionary<string, TelemetryArchivePoint>();
            var allRawStreamIds = new List<string>(totalRawEntries);
            var baseTime = DateTimeOffset.UtcNow;

            TelemetryArchivePoint GeneratePoint(int index, string prefix) => new()
            {
                EventId = Guid.NewGuid().ToString("D"),
                RiderId = $"rider-{prefix}-{10 + (index % 15)}",
                Timestamp = baseTime.AddMilliseconds(index * 50).ToString("o", CultureInfo.InvariantCulture),
                Lat = Math.Round(13.750000 + (index * 0.000050), 6),
                Lng = Math.Round(100.500000 + (index * 0.000050), 6),
                Accuracy = Math.Round(3.0 + (index % 5) * 0.5, 2)
            };

            NameValueEntry[] PointToValues(TelemetryArchivePoint p) => new NameValueEntry[]
            {
                new("eventId", p.EventId),
                new("riderId", p.RiderId),
                new("timestamp", p.Timestamp),
                new("lat", p.Lat.ToString("F6", CultureInfo.InvariantCulture)),
                new("lng", p.Lng.ToString("F6", CultureInfo.InvariantCulture)),
                new("accuracy", p.Accuracy.ToString("F2", CultureInfo.InvariantCulture))
            };

            // --- Generate Batch 1 Entries (300 raw: 297 unique + 3 in-batch dupes) ---
            var batch1UniquePoints = new List<TelemetryArchivePoint>(297);
            for (int i = 0; i < 297; i++)
            {
                var pt = GeneratePoint(i, "b1");
                batch1UniquePoints.Add(pt);
                sourceMap[pt.EventId] = pt;
            }

            var batch1RawPoints = new List<TelemetryArchivePoint>(batch1Count);
            batch1RawPoints.AddRange(batch1UniquePoints);
            // In-batch duplicates: duplicate points 10, 50, 100
            batch1RawPoints.Add(batch1UniquePoints[10]);
            batch1RawPoints.Add(batch1UniquePoints[50]);
            batch1RawPoints.Add(batch1UniquePoints[100]);

            foreach (var pt in batch1RawPoints)
            {
                var id = await redisDb.StreamAddAsync(TelemetryArchiveWorker.StreamKey, PointToValues(pt));
                allRawStreamIds.Add(id.ToString());
            }

            // --- Generate Batch 2 Entries (300 raw: 296 unique + 2 cross-batch from B1 + 2 in-batch dupes) ---
            var batch2UniquePoints = new List<TelemetryArchivePoint>(296);
            for (int i = 0; i < 296; i++)
            {
                var pt = GeneratePoint(300 + i, "b2");
                batch2UniquePoints.Add(pt);
                sourceMap[pt.EventId] = pt;
            }

            var batch2RawPoints = new List<TelemetryArchivePoint>(batch2Count);
            batch2RawPoints.AddRange(batch2UniquePoints);
            // Cross-batch duplicates: re-use event from Batch 1 (point 20 and 80)
            batch2RawPoints.Add(batch1UniquePoints[20]);
            batch2RawPoints.Add(batch1UniquePoints[80]);
            // In-batch duplicates: duplicate batch2 points 15 and 75
            batch2RawPoints.Add(batch2UniquePoints[15]);
            batch2RawPoints.Add(batch2UniquePoints[75]);

            foreach (var pt in batch2RawPoints)
            {
                var id = await redisDb.StreamAddAsync(TelemetryArchiveWorker.StreamKey, PointToValues(pt));
                allRawStreamIds.Add(id.ToString());
            }

            // --- Generate Batch 3 Entries (150 raw: 149 unique + 1 cross-batch from B2) ---
            var batch3UniquePoints = new List<TelemetryArchivePoint>(149);
            for (int i = 0; i < 149; i++)
            {
                var pt = GeneratePoint(600 + i, "b3");
                batch3UniquePoints.Add(pt);
                sourceMap[pt.EventId] = pt;
            }

            var batch3RawPoints = new List<TelemetryArchivePoint>(batch3Count);
            batch3RawPoints.AddRange(batch3UniquePoints);
            // Cross-batch duplicate: re-use event from Batch 2 (point 50)
            batch3RawPoints.Add(batch2UniquePoints[50]);

            foreach (var pt in batch3RawPoints)
            {
                var id = await redisDb.StreamAddAsync(TelemetryArchiveWorker.StreamKey, PointToValues(pt));
                allRawStreamIds.Add(id.ToString());
            }

            // Verify Stream injection counts
            const int expectedTotalUnique = 297 + 296 + 149; // 742
            Assert.Equal(totalRawEntries, allRawStreamIds.Count); // 750
            Assert.Equal(expectedTotalUnique, sourceMap.Count);   // 742
            Assert.Equal(totalRawEntries, await redisDb.StreamLengthAsync(TelemetryArchiveWorker.StreamKey));

            // =========================================================================
            // STEP 4: Worker Execution Across Continuous Multiple Batches
            // =========================================================================
            var worker = new TelemetryArchiveWorker(
                redis,
                storage,
                new NullLogger<TelemetryArchiveWorker>(),
                emptyStreamDelay: TimeSpan.FromMilliseconds(50),
                maxBatchEntries: 300,
                maxWaitInterval: TimeSpan.FromSeconds(1));

            await worker.InitializeCheckpointAsync();
            Assert.Equal("0-0", worker.CurrentCheckpoint);

            // --- Batch 1 Processing ---
            var b1 = await worker.CollectBatchAsync();
            Assert.NotNull(b1);
            Assert.Equal(batch1Count, b1.RawEntries.Count);         // 300 raw
            Assert.Equal(297, b1.DeduplicatedEntries.Count);         // 297 unique (3 in-batch dupes filtered)
            Assert.Equal(allRawStreamIds[0], b1.FirstStreamId);
            Assert.Equal(allRawStreamIds[299], b1.LastStreamId);

            await worker.ProcessBatchPipelineAsync(b1);
            Assert.Equal(b1.LastStreamId, worker.CurrentCheckpoint);
            Assert.Equal(b1.LastStreamId, (await redisDb.StringGetAsync(TelemetryArchiveWorker.CheckpointKey)).ToString());
            Assert.True((await redisDb.StringGetAsync(TelemetryArchiveWorker.PendingBatchKey)).IsNull);

            // --- Batch 2 Processing ---
            var b2 = await worker.CollectBatchAsync();
            Assert.NotNull(b2);
            Assert.Equal(batch2Count, b2.RawEntries.Count);         // 300 raw
            Assert.Equal(296, b2.DeduplicatedEntries.Count);         // 296 unique (2 cross-batch + 2 in-batch dupes filtered)
            Assert.Equal(allRawStreamIds[300], b2.FirstStreamId);
            Assert.Equal(allRawStreamIds[599], b2.LastStreamId);

            // Verify strict sequential cursor boundary between Batch 1 and Batch 2
            Assert.True(TelemetryArchiveWorker.CompareStreamIds(b1.LastStreamId, b2.FirstStreamId) < 0,
                $"Batch 2 FirstStreamId ({b2.FirstStreamId}) must be strictly greater than Batch 1 LastStreamId ({b1.LastStreamId})");

            await worker.ProcessBatchPipelineAsync(b2);
            Assert.Equal(b2.LastStreamId, worker.CurrentCheckpoint);
            Assert.Equal(b2.LastStreamId, (await redisDb.StringGetAsync(TelemetryArchiveWorker.CheckpointKey)).ToString());
            Assert.True((await redisDb.StringGetAsync(TelemetryArchiveWorker.PendingBatchKey)).IsNull);

            // --- Batch 3 Processing (Partial batch flushed via 1-second timeout) ---
            var b3_first = await worker.CollectBatchAsync();
            Assert.Null(b3_first); // Buffered 150 entries, waiting for 300 or timeout
            Assert.Equal(batch3Count, worker.BufferedCount);

            await Task.Delay(TimeSpan.FromMilliseconds(1100)); // wait for timeout
            var b3 = await worker.CollectBatchAsync();
            Assert.NotNull(b3);
            Assert.Equal(batch3Count, b3.RawEntries.Count);         // 150 raw
            Assert.Equal(149, b3.DeduplicatedEntries.Count);         // 149 unique (1 cross-batch dupe filtered)
            Assert.Equal(allRawStreamIds[600], b3.FirstStreamId);
            Assert.Equal(allRawStreamIds[749], b3.LastStreamId);

            // Verify strict sequential cursor boundary between Batch 2 and Batch 3
            Assert.True(TelemetryArchiveWorker.CompareStreamIds(b2.LastStreamId, b3.FirstStreamId) < 0,
                $"Batch 3 FirstStreamId ({b3.FirstStreamId}) must be strictly greater than Batch 2 LastStreamId ({b2.LastStreamId})");

            await worker.ProcessBatchPipelineAsync(b3);
            Assert.Equal(b3.LastStreamId, worker.CurrentCheckpoint);
            Assert.Equal(b3.LastStreamId, (await redisDb.StringGetAsync(TelemetryArchiveWorker.CheckpointKey)).ToString());
            Assert.True((await redisDb.StringGetAsync(TelemetryArchiveWorker.PendingBatchKey)).IsNull);

            // =========================================================================
            // STEP 5: Final Checkpoint & Pipeline Completeness Verification
            // =========================================================================
            var finalCheckpoint = await redisDb.StringGetAsync(TelemetryArchiveWorker.CheckpointKey);
            Assert.Equal(allRawStreamIds[^1], finalCheckpoint.ToString());
            Assert.Equal(allRawStreamIds[^1], worker.CurrentCheckpoint);

            var pendingFinal = await redisDb.StringGetAsync(TelemetryArchiveWorker.PendingBatchKey);
            Assert.True(pendingFinal.IsNull, "telemetry:archiver:pending_batch must be nil upon normal completion.");

            var streamRetained = await redisDb.StreamLengthAsync(TelemetryArchiveWorker.StreamKey);
            Assert.Equal(totalRawEntries, streamRetained); // Stream is preserved

            // =========================================================================
            // STEP 6: Full Multi-Batch Reconciliation & Data Equivalence Audit
            // =========================================================================
            var batchObjects = new[]
            {
                (Batch: b1, ExpectedRecords: 297),
                (Batch: b2, ExpectedRecords: 296),
                (Batch: b3, ExpectedRecords: 149)
            };

            var allArchivedEventIds = new HashSet<string>();
            int totalArchivedRecords = 0;

            foreach (var (b, expectedRecs) in batchObjects)
            {
                var objKey = b.GenerateObjectKey();
                bool exists = await storage.ObjectExistsAsync(objKey);
                Assert.True(exists, $"MinIO archive object '{objKey}' must exist.");

                byte[] objBytes = Array.Empty<byte>();
                await storage.GetArchiveObjectAsync(objKey, s =>
                {
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    objBytes = ms.ToArray();
                });
                Assert.NotEmpty(objBytes);

                using var decompressInput = new MemoryStream(objBytes);
                using var gzip = new GZipStream(decompressInput, CompressionMode.Decompress);
                using var reader = new StreamReader(gzip, Encoding.UTF8);
                var text = await reader.ReadToEndAsync();

                var lines = text.TrimEnd('\n').Split('\n');
                Assert.Equal(expectedRecs, lines.Length);
                totalArchivedRecords += lines.Length;

                for (int i = 0; i < lines.Length; i++)
                {
                    var pt = JsonSerializer.Deserialize<TelemetryArchivePoint>(lines[i]);
                    Assert.NotNull(pt);

                    // Zero duplicates in final archives
                    Assert.True(allArchivedEventIds.Add(pt.EventId),
                        $"Duplicate eventId '{pt.EventId}' found across archive objects!");

                    // Zero extra events (every event must exist in sourceMap)
                    Assert.True(sourceMap.TryGetValue(pt.EventId, out var src),
                        $"Archived eventId '{pt.EventId}' not found in sourceMap!");

                    // Field-by-field 100% precision comparison
                    Assert.Equal(src.RiderId, pt.RiderId);
                    Assert.Equal(src.Timestamp, pt.Timestamp);
                    Assert.Equal(src.Lat, pt.Lat, 6);
                    Assert.Equal(src.Lng, pt.Lng, 6);
                    Assert.Equal(src.Accuracy, pt.Accuracy, 2);
                }
            }

            // Assert contract: Total Archived Records == Expected Unique Records (742)
            Assert.Equal(expectedTotalUnique, totalArchivedRecords);
            Assert.Equal(expectedTotalUnique, allArchivedEventIds.Count);

            // Zero missing events: Every unique event injected must be in the archive
            foreach (var srcId in sourceMap.Keys)
            {
                Assert.Contains(srcId, allArchivedEventIds);
            }

            // =========================================================================
            // STEP 7: Reboot Idempotency After Multi-Batch Completion
            // =========================================================================
            var freshWorker = new TelemetryArchiveWorker(
                redis,
                storage,
                new NullLogger<TelemetryArchiveWorker>());

            await freshWorker.InitializeCheckpointAsync();
            Assert.Equal(allRawStreamIds[^1], freshWorker.CurrentCheckpoint);

            var extraBatch = await freshWorker.CollectBatchAsync();
            Assert.Null(extraBatch); // No new batch collected (cursor is already at end)
            Assert.False(freshWorker.IsHalted);
        }

        private sealed class TrackingTelemetryArchiveStorage : ITelemetryArchiveStorage
        {
            private readonly ITelemetryArchiveStorage _inner;
            public int PutObjectCallCount { get; private set; }

            public TrackingTelemetryArchiveStorage(ITelemetryArchiveStorage inner)
            {
                _inner = inner;
            }

            public string BucketName => _inner.BucketName;

            public Task EnsureArchiveBucketExistsAsync(CancellationToken ct = default) =>
                _inner.EnsureArchiveBucketExistsAsync(ct);

            public Task<string> PutArchiveObjectAsync(Stream dataStream, string objectKey, string contentType = "application/x-ndjson", CancellationToken ct = default)
            {
                PutObjectCallCount++;
                return _inner.PutArchiveObjectAsync(dataStream, objectKey, contentType, ct);
            }

            public Task<bool> ObjectExistsAsync(string objectKey, CancellationToken ct = default) =>
                _inner.ObjectExistsAsync(objectKey, ct);

            public Task GetArchiveObjectAsync(string objectKey, Action<Stream> callback, CancellationToken ct = default) =>
                _inner.GetArchiveObjectAsync(objectKey, callback, ct);
        }
    }
}

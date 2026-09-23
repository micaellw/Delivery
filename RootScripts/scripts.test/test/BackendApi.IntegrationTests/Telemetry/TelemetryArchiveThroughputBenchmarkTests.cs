using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BackendApi.Services.BackgroundWorkers.Jobs;
using BackendApi.Services.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Xunit;
using Xunit.Abstractions;

namespace BackendApi.IntegrationTests.Telemetry
{
    public class TelemetryArchiveThroughputBenchmarkTests
    {
        private const string RedisConnectionString = "127.0.0.1:6389,password=Password123!,abortConnect=false,allowAdmin=true";
        private const string MinioEndpoint = "127.0.0.1:9002";
        private const string MinioAccessKey = "minioadmin";
        private const string MinioSecretKey = "miniopassword123";
        private const string ArchiveBucketName = "delivery-telemetry-archive";

        private readonly ITestOutputHelper _output;
        private readonly IConfiguration _configuration;

        static TelemetryArchiveThroughputBenchmarkTests()
        {
            // CRITICAL: Guarantee Invariant/Gregorian calendar for AWS S3 SigV4
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        }

        public TelemetryArchiveThroughputBenchmarkTests(ITestOutputHelper output)
        {
            _output = output;
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

        // =========================================================================
        // STEP T0: Benchmark Harness Preparation & Smoke Test
        // =========================================================================
        [Fact]
        public async Task Step_T0_HarnessSmokeTest_50Entries_PipelineVerification()
        {
            _output.WriteLine("=========================================================================");
            _output.WriteLine("Step T0: Benchmark Harness Preparation & Verification Smoke Test");
            _output.WriteLine("=========================================================================");

            // 1. Connect to Real Docker Redis and Real Docker MinIO
            var redis = await ConnectionMultiplexer.ConnectAsync(RedisConnectionString);
            var redisDb = redis.GetDatabase();

            var innerStorage = new MinioTelemetryArchiveStorage(_configuration, new NullLogger<MinioTelemetryArchiveStorage>());
            await innerStorage.EnsureArchiveBucketExistsAsync();

            var instrumentedStorage = new InstrumentedTelemetryArchiveStorage(innerStorage);

            // 2. Clean Slate Reset
            await ResetEnvironmentAsync(redis, redisDb);

            // 3. Generate Test Telemetry (50 entries: 48 unique + 2 deliberate duplicates)
            const int totalRaw = 50;
            const int uniqueCount = 48;
            var points = GenerateTestPoints(uniqueCount, duplicateCount: 2);
            var sourceMap = points.DistinctBy(p => p.EventId).ToDictionary(p => p.EventId);

            // 4. Inject into Redis Stream and measure ingestion time
            var injectSw = Stopwatch.StartNew();
            var streamIds = new List<string>(totalRaw);
            foreach (var pt in points)
            {
                var id = await redisDb.StreamAddAsync(TelemetryArchiveWorker.StreamKey, ToNameValueEntries(pt));
                streamIds.Add(id.ToString());
            }
            injectSw.Stop();

            double inputRate = totalRaw / injectSw.Elapsed.TotalSeconds;
            _output.WriteLine($"[T0 Smoke] Injected {totalRaw} entries in {injectSw.ElapsedMilliseconds} ms ({inputRate:F1} pts/sec).");

            // 5. Query Redis Memory Before Archival
            var redisMemBefore = await GetRedisMemoryStatsAsync(redis);

            // 6. Instantiate Archiver Worker and Process Batch
            var worker = new TelemetryArchiveWorker(
                redis,
                instrumentedStorage,
                new NullLogger<TelemetryArchiveWorker>(),
                emptyStreamDelay: TimeSpan.FromMilliseconds(50),
                maxBatchEntries: 100,
                maxWaitInterval: TimeSpan.FromSeconds(1));

            await worker.InitializeCheckpointAsync();
            Assert.Equal("0-0", worker.CurrentCheckpoint);

            // Capture Worker process metrics before drain
            var gc0Before = GC.CollectionCount(0);
            var gc1Before = GC.CollectionCount(1);
            var gc2Before = GC.CollectionCount(2);
            var memBefore = GC.GetTotalMemory(forceFullCollection: false);
            var procBefore = Process.GetCurrentProcess();
            var cpuTimeBefore = procBefore.TotalProcessorTime;

            var drainSw = Stopwatch.StartNew();
            var batch = await worker.CollectBatchAsync();

            // Handle partial batch timeout if needed
            if (batch == null && worker.BufferedCount > 0)
            {
                await Task.Delay(1100);
                batch = await worker.CollectBatchAsync();
            }

            Assert.NotNull(batch);
            Assert.Equal(totalRaw, batch.RawEntries.Count);
            Assert.Equal(uniqueCount, batch.DeduplicatedEntries.Count);

            await worker.ProcessBatchPipelineAsync(batch);
            drainSw.Stop();

            // Capture Worker process metrics after drain
            var drainElapsed = drainSw.Elapsed.TotalSeconds;
            double drainThroughput = totalRaw / drainElapsed;
            var procAfter = Process.GetCurrentProcess();
            var cpuTimeDelta = (procAfter.TotalProcessorTime - cpuTimeBefore).TotalMilliseconds;
            var memAfter = GC.GetTotalMemory(forceFullCollection: false);
            var gc0Delta = GC.CollectionCount(0) - gc0Before;
            var gc1Delta = GC.CollectionCount(1) - gc1Before;
            var gc2Delta = GC.CollectionCount(2) - gc2Before;

            var redisMemAfter = await GetRedisMemoryStatsAsync(redis);

            // 7. Checkpoint, Pending, and Stream Retention Invariants
            var checkpoint = (await redisDb.StringGetAsync(TelemetryArchiveWorker.CheckpointKey)).ToString();
            Assert.Equal(streamIds[^1], checkpoint);
            Assert.Equal(streamIds[^1], worker.CurrentCheckpoint);

            var pending = await redisDb.StringGetAsync(TelemetryArchiveWorker.PendingBatchKey);
            Assert.True(pending.IsNull, "pending_batch must be deleted upon normal completion.");

            var streamLen = await redisDb.StreamLengthAsync(TelemetryArchiveWorker.StreamKey);
            Assert.Equal(totalRaw, streamLen);

            // 8. Reconciliation & Data Equivalence Audit
            var objKey = batch.GenerateObjectKey();
            bool objExists = await instrumentedStorage.ObjectExistsAsync(objKey);
            Assert.True(objExists, $"MinIO object {objKey} must exist.");

            var archivedPoints = await ReadArchivedPointsAsync(instrumentedStorage, objKey);
            Assert.Equal(uniqueCount, archivedPoints.Count);

            var archivedEventIds = new HashSet<string>();
            foreach (var pt in archivedPoints)
            {
                Assert.True(archivedEventIds.Add(pt.EventId), $"Duplicate eventId {pt.EventId} in archive!");
                Assert.True(sourceMap.TryGetValue(pt.EventId, out var src), $"Archived eventId {pt.EventId} not in source!");
                Assert.Equal(src.RiderId, pt.RiderId);
                Assert.Equal(src.Timestamp, pt.Timestamp);
                Assert.Equal(src.Lat, pt.Lat, 6);
                Assert.Equal(src.Lng, pt.Lng, 6);
                Assert.Equal(src.Accuracy, pt.Accuracy, 2);
            }

            Assert.Equal(uniqueCount, archivedEventIds.Count);

            // 9. Format and Output Comprehensive Benchmark Metrics
            _output.WriteLine("\n-------------------------------------------------------------------------");
            _output.WriteLine("Step T0 Smoke Test Metrics Summary:");
            _output.WriteLine("-------------------------------------------------------------------------");
            _output.WriteLine($"| Metric | Value |");
            _output.WriteLine($"|---|---|");
            _output.WriteLine($"| Raw Stream Entries Injected | {totalRaw} |");
            _output.WriteLine($"| Unique Event IDs | {uniqueCount} |");
            _output.WriteLine($"| Injected Duplicates | 2 |");
            _output.WriteLine($"| Archived Records in MinIO | {archivedPoints.Count} |");
            _output.WriteLine($"| Duplicate Events in Archive | 0 (Verified) |");
            _output.WriteLine($"| Missing Events in Archive | 0 (Verified) |");
            _output.WriteLine($"| Extra Events in Archive | 0 (Verified) |");
            _output.WriteLine($"| Input Ingestion Time | {injectSw.ElapsedMilliseconds} ms ({inputRate:F1} pts/s) |");
            _output.WriteLine($"| Drain Processing Time | {drainSw.ElapsedMilliseconds} ms ({drainThroughput:F1} pts/s) |");
            _output.WriteLine($"| MinIO Upload Latency | {instrumentedStorage.TotalUploadMs} ms (Calls: {instrumentedStorage.UploadCallCount}) |");
            _output.WriteLine($"| MinIO Compressed Bytes Written | {instrumentedStorage.TotalBytesUploaded} bytes |");
            _output.WriteLine($"| Checkpoint Lag | 0 (Cursor = {checkpoint}) |");
            _output.WriteLine($"| Pending Key Status | Cleared (nil) |");
            _output.WriteLine($"| Stream Preservation | {streamLen} / {totalRaw} entries intact |");
            _output.WriteLine($"| Worker CPU Delta | {cpuTimeDelta:F1} ms |");
            _output.WriteLine($"| Worker Memory Delta | {(memAfter - memBefore) / 1024.0:F1} KiB |");
            _output.WriteLine($"| Worker GC Collections (Gen 0/1/2) | {gc0Delta}/{gc1Delta}/{gc2Delta} |");
            _output.WriteLine($"| Redis Used Memory (Before -> After) | {redisMemBefore.UsedMemoryHuman} -> {redisMemAfter.UsedMemoryHuman} |");
            _output.WriteLine("-------------------------------------------------------------------------");
            _output.WriteLine("[T0 Smoke Test] PASS - Harness ready for Step T1.\n");
        }

        // =========================================================================
        // STEP T1: Scenario 1: Steady-State Concurrent Flow (500 pts/s x 10 sec)
        // =========================================================================
        [Fact]
        public async Task Step_T1_Scenario1_SteadyStateConcurrent_500PtsPerSec_10Sec()
        {
            _output.WriteLine("=========================================================================");
            _output.WriteLine("Step T1: Scenario 1 — Steady-State Concurrent Flow Benchmark");
            _output.WriteLine("Target: 500 points/sec for 10 seconds (~5,000 points) with concurrent archival");
            _output.WriteLine("=========================================================================");

            // 1. Connect to Real Docker Redis and Real Docker MinIO
            var redis = await ConnectionMultiplexer.ConnectAsync(RedisConnectionString);
            var redisDb = redis.GetDatabase();

            var innerStorage = new MinioTelemetryArchiveStorage(_configuration, new NullLogger<MinioTelemetryArchiveStorage>());
            await innerStorage.EnsureArchiveBucketExistsAsync();

            var instrumentedStorage = new InstrumentedTelemetryArchiveStorage(innerStorage);

            // 2. Clean Slate Reset
            await ResetEnvironmentAsync(redis, redisDb);

            // 3. Pre-generate 5,000 telemetry points (4,950 unique + 50 deliberate duplicates = 1%)
            const int totalRaw = 5000;
            const int uniqueCount = 4950;
            const int duplicateCount = 50;
            var points = GenerateTestPoints(uniqueCount, duplicateCount);
            var sourceMap = points.DistinctBy(p => p.EventId).ToDictionary(p => p.EventId);

            // 4. Set up Archiver Worker
            var worker = new TelemetryArchiveWorker(
                redis,
                instrumentedStorage,
                new NullLogger<TelemetryArchiveWorker>(),
                emptyStreamDelay: TimeSpan.FromMilliseconds(50),
                maxBatchEntries: 1000,
                maxWaitInterval: TimeSpan.FromSeconds(1));

            await worker.InitializeCheckpointAsync();
            Assert.Equal("0-0", worker.CurrentCheckpoint);

            // 5. Query Redis Memory Before Test
            var redisMemBefore = await GetRedisMemoryStatsAsync(redis);

            // Capture initial Process resource state
            var procBefore = Process.GetCurrentProcess();
            var cpuTimeBefore = procBefore.TotalProcessorTime;
            var memBefore = GC.GetTotalMemory(forceFullCollection: false);
            var gc0Before = GC.CollectionCount(0);
            var gc1Before = GC.CollectionCount(1);
            var gc2Before = GC.CollectionCount(2);

            // Diagnostics and Tracking Structures
            var streamIds = new ConcurrentQueue<string>();
            var allInjectedStreamIds = new List<string>(totalRaw);
            var injectionTimestamps = new ConcurrentDictionary<string, long>();
            var batchMetrics = new List<(string BatchKey, int RawCount, int DedupCount, long PendingDurationMs)>();
            var checkpointLags = new List<(double ElapsedSec, int LagEntries)>();
            var endToEndLatenciesMs = new List<double>();

            var producerFinished = false;
            var archiverStopwatch = Stopwatch.StartNew();

            // 6. Start Background Archiver Task (Runs Concurrently with Producer)
            var archiverTask = Task.Run(async () =>
            {
                while (!producerFinished || worker.CurrentCheckpoint != (allInjectedStreamIds.Count == totalRaw ? allInjectedStreamIds[^1] : "NOT_READY"))
                {
                    var batch = await worker.CollectBatchAsync();
                    if (batch != null)
                    {
                        var pendingSw = Stopwatch.StartNew();
                        await worker.ProcessBatchPipelineAsync(batch);
                        pendingSw.Stop();

                        var commitTicks = Stopwatch.GetTimestamp();
                        foreach (var entry in batch.DeduplicatedEntries)
                        {
                            var entryIdStr = entry.Id.ToString();
                            if (injectionTimestamps.TryGetValue(entryIdStr, out var injTicks))
                            {
                                var latencyMs = (commitTicks - injTicks) * 1000.0 / Stopwatch.Frequency;
                                endToEndLatenciesMs.Add(latencyMs);
                            }
                        }

                        batchMetrics.Add((batch.GenerateObjectKey(), batch.RawEntries.Count, batch.DeduplicatedEntries.Count, pendingSw.ElapsedMilliseconds));
                    }
                    else
                    {
                        // Check for partial batch timeout after producer finished
                        if (producerFinished && worker.BufferedCount > 0)
                        {
                            await Task.Delay(1100);
                            var partialBatch = await worker.CollectBatchAsync();
                            if (partialBatch != null)
                            {
                                var pendingSw = Stopwatch.StartNew();
                                await worker.ProcessBatchPipelineAsync(partialBatch);
                                pendingSw.Stop();

                                var commitTicks = Stopwatch.GetTimestamp();
                                foreach (var entry in partialBatch.DeduplicatedEntries)
                                {
                                    var entryIdStr = entry.Id.ToString();
                                    if (injectionTimestamps.TryGetValue(entryIdStr, out var injTicks))
                                    {
                                        var latencyMs = (commitTicks - injTicks) * 1000.0 / Stopwatch.Frequency;
                                        endToEndLatenciesMs.Add(latencyMs);
                                    }
                                }

                                batchMetrics.Add((partialBatch.GenerateObjectKey(), partialBatch.RawEntries.Count, partialBatch.DeduplicatedEntries.Count, pendingSw.ElapsedMilliseconds));
                            }
                        }
                        else
                        {
                            await Task.Delay(20);
                        }
                    }

                    // Sample checkpoint lag during run
                    if (allInjectedStreamIds.Count > 0)
                    {
                        var lastInjected = allInjectedStreamIds[^1];
                        var curCheckpoint = worker.CurrentCheckpoint;
                        int lag = allInjectedStreamIds.Count - (curCheckpoint == "0-0" ? 0 : (allInjectedStreamIds.IndexOf(curCheckpoint) + 1));
                        checkpointLags.Add((archiverStopwatch.Elapsed.TotalSeconds, Math.Max(0, lag)));
                    }
                }
            });

            // 7. Run Paced Producer: 50 points every 100ms = 500 points/sec for 10 seconds
            const int batchSize = 50;
            const int targetIntervalMs = 100;
            const int totalIntervals = totalRaw / batchSize; // 100 intervals

            var producerSw = Stopwatch.StartNew();
            for (int interval = 0; interval < totalIntervals; interval++)
            {
                var intervalStart = Stopwatch.GetTimestamp();
                var tasks = new List<Task<RedisValue>>(batchSize);

                for (int i = 0; i < batchSize; i++)
                {
                    int pointIndex = interval * batchSize + i;
                    var pt = points[pointIndex];
                    tasks.Add(redisDb.StreamAddAsync(TelemetryArchiveWorker.StreamKey, ToNameValueEntries(pt)));
                }

                var results = await Task.WhenAll(tasks);
                var injTimestamp = Stopwatch.GetTimestamp();
                foreach (var res in results)
                {
                    var idStr = res.ToString();
                    streamIds.Enqueue(idStr);
                    allInjectedStreamIds.Add(idStr);
                    injectionTimestamps[idStr] = injTimestamp;
                }

                // Sleep remainder of 100ms interval
                var elapsedMs = (Stopwatch.GetTimestamp() - intervalStart) * 1000.0 / Stopwatch.Frequency;
                var sleepMs = (int)(targetIntervalMs - elapsedMs);
                if (sleepMs > 0)
                {
                    await Task.Delay(sleepMs);
                }
            }
            producerSw.Stop();
            producerFinished = true;

            // Wait for Archiver to finish draining all remaining points
            await archiverTask;
            archiverStopwatch.Stop();

            // Capture final Process resource state
            var procAfter = Process.GetCurrentProcess();
            var cpuTimeDelta = (procAfter.TotalProcessorTime - cpuTimeBefore).TotalMilliseconds;
            var memAfter = GC.GetTotalMemory(forceFullCollection: false);
            var gc0Delta = GC.CollectionCount(0) - gc0Before;
            var gc1Delta = GC.CollectionCount(1) - gc1Before;
            var gc2Delta = GC.CollectionCount(2) - gc2Before;

            var redisMemAfter = await GetRedisMemoryStatsAsync(redis);

            // 8. Gate Assertions & Invariants
            Assert.Equal(totalRaw, allInjectedStreamIds.Count);
            Assert.Equal(allInjectedStreamIds[^1], worker.CurrentCheckpoint);

            var finalCheckpointKey = (await redisDb.StringGetAsync(TelemetryArchiveWorker.CheckpointKey)).ToString();
            Assert.Equal(allInjectedStreamIds[^1], finalCheckpointKey);

            var finalPending = await redisDb.StringGetAsync(TelemetryArchiveWorker.PendingBatchKey);
            Assert.True(finalPending.IsNull, "telemetry:archiver:pending_batch must be nil upon completion.");

            var streamLen = await redisDb.StreamLengthAsync(TelemetryArchiveWorker.StreamKey);
            Assert.Equal(totalRaw, streamLen);

            // 9. Full Reconciliation & Data Equivalence Audit
            var allArchivedEventIds = new HashSet<string>();
            var allArchivedRecords = new List<TelemetryArchivePoint>();

            foreach (var bm in batchMetrics)
            {
                var pointsInBatch = await ReadArchivedPointsAsync(instrumentedStorage, bm.BatchKey);
                allArchivedRecords.AddRange(pointsInBatch);

                foreach (var pt in pointsInBatch)
                {
                    Assert.True(allArchivedEventIds.Add(pt.EventId), $"Duplicate eventId {pt.EventId} across archives!");
                    Assert.True(sourceMap.TryGetValue(pt.EventId, out var src), $"Archived eventId {pt.EventId} not in source!");
                    Assert.Equal(src.RiderId, pt.RiderId);
                    Assert.Equal(src.Timestamp, pt.Timestamp);
                    Assert.Equal(src.Lat, pt.Lat, 6);
                    Assert.Equal(src.Lng, pt.Lng, 6);
                    Assert.Equal(src.Accuracy, pt.Accuracy, 2);
                }
            }

            Assert.Equal(uniqueCount, allArchivedRecords.Count);
            Assert.Equal(uniqueCount, allArchivedEventIds.Count);

            // 10. Compute Benchmark Statistics
            double actualProducerRate = totalRaw / producerSw.Elapsed.TotalSeconds;
            double actualArchiverRate = totalRaw / archiverStopwatch.Elapsed.TotalSeconds;
            double maxLag = checkpointLags.Count > 0 ? checkpointLags.Max(c => c.LagEntries) : 0;
            double avgLag = checkpointLags.Count > 0 ? checkpointLags.Average(c => c.LagEntries) : 0;
            double avgPendingMs = batchMetrics.Count > 0 ? batchMetrics.Average(b => b.PendingDurationMs) : 0;
            double maxPendingMs = batchMetrics.Count > 0 ? batchMetrics.Max(b => b.PendingDurationMs) : 0;
            double avgUploadMs = instrumentedStorage.UploadLatenciesMs.Count > 0 ? instrumentedStorage.UploadLatenciesMs.Average() : 0;
            long maxUploadMs = instrumentedStorage.UploadLatenciesMs.Count > 0 ? instrumentedStorage.UploadLatenciesMs.Max() : 0;
            double avgEndToEndMs = endToEndLatenciesMs.Count > 0 ? endToEndLatenciesMs.Average() : 0;
            double maxEndToEndMs = endToEndLatenciesMs.Count > 0 ? endToEndLatenciesMs.Max() : 0;

            // Target comparison interpretation
            string targetComparison;
            if (actualArchiverRate > 250 * 1.1)
                targetComparison = $"Archiver > 250 pts/s ({actualArchiverRate:F1} pts/s, Throughput margin +{(actualArchiverRate - 250) / 250 * 100:F1}%)";
            else if (actualArchiverRate >= 250 * 0.9)
                targetComparison = $"Archiver ≈ 250 pts/s ({actualArchiverRate:F1} pts/s, Meets target in this benchmark)";
            else
                targetComparison = $"Archiver < 250 pts/s ({actualArchiverRate:F1} pts/s, Below Producer Design Target -> analyze bottleneck)";

            // 11. Format and Output Comprehensive Report
            _output.WriteLine("\n-------------------------------------------------------------------------");
            _output.WriteLine("Step T1: Scenario 1 Steady-State Concurrent Flow Benchmark Results");
            _output.WriteLine("-------------------------------------------------------------------------");
            _output.WriteLine($"| Metric | Value |");
            _output.WriteLine($"|---|---|");
            _output.WriteLine($"| Total Raw Stream Entries Injected | {totalRaw} |");
            _output.WriteLine($"| Unique Event IDs | {uniqueCount} |");
            _output.WriteLine($"| Injected Duplicates | {duplicateCount} (1.0%) |");
            _output.WriteLine($"| Archived Records in MinIO | {allArchivedRecords.Count} |");
            _output.WriteLine($"| Missing Events in Archive | 0 (Verified 100%) |");
            _output.WriteLine($"| Extra Events in Archive | 0 (Verified 100%) |");
            _output.WriteLine($"| Duplicate Events in Archive | 0 (Verified 100%) |");
            _output.WriteLine($"| Producer Duration | {producerSw.Elapsed.TotalSeconds:F2} s |");
            _output.WriteLine($"| Actual Producer Input Rate | {actualProducerRate:F1} points/sec |");
            _output.WriteLine($"| Archiver Drain Duration | {archiverStopwatch.Elapsed.TotalSeconds:F2} s |");
            _output.WriteLine($"| Actual Archiver Drain Throughput | {actualArchiverRate:F1} points/sec |");
            _output.WriteLine($"| Producer Design Target (250 pts/s) | {targetComparison} |");
            _output.WriteLine($"| Batches / Objects Created | {batchMetrics.Count} objects |");
            _output.WriteLine($"| Real-time Checkpoint Lag (Avg / Max) | {avgLag:F1} / {maxLag} entries |");
            _output.WriteLine($"| Pending Key Duration (Avg / Max) | {avgPendingMs:F1} ms / {maxPendingMs} ms |");
            _output.WriteLine($"| End-to-End Latency (Avg / Max) | {avgEndToEndMs:F1} ms / {maxEndToEndMs:F1} ms |");
            _output.WriteLine($"| MinIO Upload Latency (Avg / Max) | {avgUploadMs:F1} ms / {maxUploadMs} ms |");
            _output.WriteLine($"| MinIO Total Compressed Bytes Written | {instrumentedStorage.TotalBytesUploaded:N0} bytes |");
            _output.WriteLine($"| Stream Preservation | {streamLen} / {totalRaw} intact in Redis |");
            _output.WriteLine($"| Checkpoint Status | {worker.CurrentCheckpoint} (All committed) |");
            _output.WriteLine($"| Pending Key Status | (nil) |");
            _output.WriteLine($"| Worker CPU Delta | {cpuTimeDelta:F1} ms |");
            _output.WriteLine($"| Worker Memory Delta | {(memAfter - memBefore) / 1024.0:F1} KiB |");
            _output.WriteLine($"| Worker GC Collections (Gen 0/1/2) | {gc0Delta}/{gc1Delta}/{gc2Delta} |");
            _output.WriteLine($"| Redis Memory (Before -> After) | {redisMemBefore.UsedMemoryHuman} -> {redisMemAfter.UsedMemoryHuman} |");
            _output.WriteLine("-------------------------------------------------------------------------");
            _output.WriteLine("[T1 Benchmark] Completed Successfully.\n");
        }

        // =========================================================================
        // STEP T3: Scenario 2: Peak Backlog Drain (10,000 entries / 10 batches)
        // =========================================================================
        [Fact]
        public async Task Step_T3_Scenario2_PeakBacklogDrain_10000Entries()
        {
            _output.WriteLine("=========================================================================");
            _output.WriteLine("Step T3: Scenario 2 — Peak Backlog Drain Benchmark");
            _output.WriteLine("Workload: 10,000 raw entries pre-populated in Redis Stream (9,800 unique + 200 dupes)");
            _output.WriteLine("Batch Config: maxBatchEntries = 1,000, maxWaitInterval = 1s");
            _output.WriteLine("=========================================================================");

            // 1. Connect to Real Docker Redis and Real Docker MinIO
            var redis = await ConnectionMultiplexer.ConnectAsync(RedisConnectionString);
            var redisDb = redis.GetDatabase();

            var innerStorage = new MinioTelemetryArchiveStorage(_configuration, new NullLogger<MinioTelemetryArchiveStorage>());
            await innerStorage.EnsureArchiveBucketExistsAsync();

            var instrumentedStorage = new InstrumentedTelemetryArchiveStorage(innerStorage);

            // 2. Clean Slate Reset
            await ResetEnvironmentAsync(redis, redisDb);

            // 3. Pre-generate 10,000 telemetry points (9,800 unique + 200 deliberate duplicates = 2.0%)
            const int totalRaw = 10000;
            const int uniqueCount = 9800;
            const int duplicateCount = 200;
            var points = GenerateTestPoints(uniqueCount, duplicateCount);
            var sourceMap = points.DistinctBy(p => p.EventId).ToDictionary(p => p.EventId);

            // 4. Pre-populate Redis Stream with all 10,000 entries BEFORE starting worker
            var prePopSw = Stopwatch.StartNew();
            var allStreamIds = new List<string>(totalRaw);
            const int chunkPipelined = 200;
            for (int i = 0; i < totalRaw; i += chunkPipelined)
            {
                var tasks = new List<Task<RedisValue>>(chunkPipelined);
                int limit = Math.Min(i + chunkPipelined, totalRaw);
                for (int j = i; j < limit; j++)
                {
                    tasks.Add(redisDb.StreamAddAsync(TelemetryArchiveWorker.StreamKey, ToNameValueEntries(points[j])));
                }
                var results = await Task.WhenAll(tasks);
                foreach (var res in results)
                {
                    allStreamIds.Add(res.ToString());
                }
            }
            prePopSw.Stop();

            double prePopRate = totalRaw / prePopSw.Elapsed.TotalSeconds;
            _output.WriteLine($"[T3 Backlog Pre-population] Injected {totalRaw} entries into Redis Stream in {prePopSw.ElapsedMilliseconds} ms ({prePopRate:F1} pts/sec).");

            var initialStreamLen = await redisDb.StreamLengthAsync(TelemetryArchiveWorker.StreamKey);
            Assert.Equal(totalRaw, initialStreamLen);

            // 5. Query Redis Memory Before Drain
            var redisMemBefore = await GetRedisMemoryStatsAsync(redis);

            // Capture initial Process resource state
            var procBefore = Process.GetCurrentProcess();
            var cpuTimeBefore = procBefore.TotalProcessorTime;
            var memBefore = GC.GetTotalMemory(forceFullCollection: false);
            var gc0Before = GC.CollectionCount(0);
            var gc1Before = GC.CollectionCount(1);
            var gc2Before = GC.CollectionCount(2);

            // 6. Set up Archiver Worker
            var worker = new TelemetryArchiveWorker(
                redis,
                instrumentedStorage,
                new NullLogger<TelemetryArchiveWorker>(),
                emptyStreamDelay: TimeSpan.FromMilliseconds(20),
                maxBatchEntries: 1000,
                maxWaitInterval: TimeSpan.FromSeconds(1));

            await worker.InitializeCheckpointAsync();
            Assert.Equal("0-0", worker.CurrentCheckpoint);

            // 7. Execute Unthrottled Peak Backlog Drain
            var batchMetrics = new List<(string BatchKey, int RawCount, int DedupCount, long PendingDurationMs)>();
            var drainSw = Stopwatch.StartNew();

            while (worker.CurrentCheckpoint != allStreamIds[^1])
            {
                var batch = await worker.CollectBatchAsync();
                if (batch != null)
                {
                    var pendingSw = Stopwatch.StartNew();
                    await worker.ProcessBatchPipelineAsync(batch);
                    pendingSw.Stop();

                    batchMetrics.Add((batch.GenerateObjectKey(), batch.RawEntries.Count, batch.DeduplicatedEntries.Count, pendingSw.ElapsedMilliseconds));
                }
                else
                {
                    // Check if remaining entries are buffered and need partial flush
                    if (worker.BufferedCount > 0)
                    {
                        await Task.Delay(1100);
                        var partialBatch = await worker.CollectBatchAsync();
                        if (partialBatch != null)
                        {
                            var pendingSw = Stopwatch.StartNew();
                            await worker.ProcessBatchPipelineAsync(partialBatch);
                            pendingSw.Stop();

                            batchMetrics.Add((partialBatch.GenerateObjectKey(), partialBatch.RawEntries.Count, partialBatch.DeduplicatedEntries.Count, pendingSw.ElapsedMilliseconds));
                        }
                    }
                    else
                    {
                        await Task.Delay(10);
                    }
                }
            }
            drainSw.Stop();

            // Capture final Process resource state
            var procAfter = Process.GetCurrentProcess();
            var cpuTimeDelta = (procAfter.TotalProcessorTime - cpuTimeBefore).TotalMilliseconds;
            var memAfter = GC.GetTotalMemory(forceFullCollection: false);
            var gc0Delta = GC.CollectionCount(0) - gc0Before;
            var gc1Delta = GC.CollectionCount(1) - gc1Before;
            var gc2Delta = GC.CollectionCount(2) - gc2Before;

            var redisMemAfter = await GetRedisMemoryStatsAsync(redis);

            // 8. Gate Assertions & Invariants
            Assert.Equal(allStreamIds[^1], worker.CurrentCheckpoint);

            var finalCheckpointKey = (await redisDb.StringGetAsync(TelemetryArchiveWorker.CheckpointKey)).ToString();
            Assert.Equal(allStreamIds[^1], finalCheckpointKey);

            var finalPending = await redisDb.StringGetAsync(TelemetryArchiveWorker.PendingBatchKey);
            Assert.True(finalPending.IsNull, "telemetry:archiver:pending_batch must be nil upon completion.");

            var streamLen = await redisDb.StreamLengthAsync(TelemetryArchiveWorker.StreamKey);
            Assert.Equal(totalRaw, streamLen);

            // 9. Full Reconciliation & Data Equivalence Audit
            var allArchivedEventIds = new HashSet<string>();
            var allArchivedRecords = new List<TelemetryArchivePoint>();

            foreach (var bm in batchMetrics)
            {
                var pointsInBatch = await ReadArchivedPointsAsync(instrumentedStorage, bm.BatchKey);
                allArchivedRecords.AddRange(pointsInBatch);

                foreach (var pt in pointsInBatch)
                {
                    Assert.True(allArchivedEventIds.Add(pt.EventId), $"Duplicate eventId {pt.EventId} across archives!");
                    Assert.True(sourceMap.TryGetValue(pt.EventId, out var src), $"Archived eventId {pt.EventId} not in source!");
                    Assert.Equal(src.RiderId, pt.RiderId);
                    Assert.Equal(src.Timestamp, pt.Timestamp);
                    Assert.Equal(src.Lat, pt.Lat, 6);
                    Assert.Equal(src.Lng, pt.Lng, 6);
                    Assert.Equal(src.Accuracy, pt.Accuracy, 2);
                }
            }

            Assert.Equal(uniqueCount, allArchivedRecords.Count);
            Assert.Equal(uniqueCount, allArchivedEventIds.Count);

            // 10. Compute Benchmark Statistics
            double drainSeconds = drainSw.Elapsed.TotalSeconds;
            double actualDrainThroughput = totalRaw / drainSeconds;
            double avgPendingMs = batchMetrics.Count > 0 ? batchMetrics.Average(b => b.PendingDurationMs) : 0;
            double maxPendingMs = batchMetrics.Count > 0 ? batchMetrics.Max(b => b.PendingDurationMs) : 0;
            double avgUploadMs = instrumentedStorage.UploadLatenciesMs.Count > 0 ? instrumentedStorage.UploadLatenciesMs.Average() : 0;
            long maxUploadMs = instrumentedStorage.UploadLatenciesMs.Count > 0 ? instrumentedStorage.UploadLatenciesMs.Max() : 0;

            // Target comparison interpretation
            string targetComparison;
            if (actualDrainThroughput > 250 * 1.1)
                targetComparison = $"Archiver > 250 pts/s ({actualDrainThroughput:F1} pts/s, Throughput margin +{(actualDrainThroughput - 250) / 250 * 100:F1}%)";
            else if (actualDrainThroughput >= 250 * 0.9)
                targetComparison = $"Archiver ≈ 250 pts/s ({actualDrainThroughput:F1} pts/s, Meets target in this benchmark)";
            else
                targetComparison = $"Archiver < 250 pts/s ({actualDrainThroughput:F1} pts/s, Below Producer Design Target -> analyze bottleneck)";

            // 11. Format and Output Comprehensive Report
            _output.WriteLine("\n-------------------------------------------------------------------------");
            _output.WriteLine("Step T3: Scenario 2 Peak Backlog Drain Benchmark Results");
            _output.WriteLine("-------------------------------------------------------------------------");
            _output.WriteLine($"| Metric | Value |");
            _output.WriteLine($"|---|---|");
            _output.WriteLine($"| Total Raw Stream Entries in Backlog | {totalRaw} |");
            _output.WriteLine($"| Unique Event IDs | {uniqueCount} |");
            _output.WriteLine($"| Injected Duplicates | {duplicateCount} (2.0%) |");
            _output.WriteLine($"| Archived Records in MinIO | {allArchivedRecords.Count} |");
            _output.WriteLine($"| Missing Events in Archive | 0 (Verified 100%) |");
            _output.WriteLine($"| Extra Events in Archive | 0 (Verified 100%) |");
            _output.WriteLine($"| Duplicate Events in Archive | 0 (Verified 100%) |");
            _output.WriteLine($"| Backlog Pre-population Duration | {prePopSw.Elapsed.TotalSeconds:F2} s ({prePopRate:F1} pts/s) |");
            _output.WriteLine($"| Archiver Drain Duration | {drainSeconds:F2} s |");
            _output.WriteLine($"| Peak Archiver Drain Throughput | {actualDrainThroughput:F1} points/sec |");
            _output.WriteLine($"| Producer Design Target (250 pts/s) | {targetComparison} |");
            _output.WriteLine($"| Batches / Objects Created | {batchMetrics.Count} objects |");
            _output.WriteLine($"| Average Records per Batch | {batchMetrics.Average(b => b.RawCount):F1} raw / {batchMetrics.Average(b => b.DedupCount):F1} dedup |");
            _output.WriteLine($"| Pending Key Duration (Avg / Max) | {avgPendingMs:F1} ms / {maxPendingMs} ms |");
            _output.WriteLine($"| MinIO Upload Latency (Avg / Max) | {avgUploadMs:F1} ms / {maxUploadMs} ms |");
            _output.WriteLine($"| MinIO Total Compressed Bytes Written | {instrumentedStorage.TotalBytesUploaded:N0} bytes |");
            _output.WriteLine($"| Stream Preservation | {streamLen} / {totalRaw} intact in Redis |");
            _output.WriteLine($"| Checkpoint Status | {worker.CurrentCheckpoint} (All committed) |");
            _output.WriteLine($"| Pending Key Status | (nil) |");
            _output.WriteLine($"| Worker CPU Delta | {cpuTimeDelta:F1} ms |");
            _output.WriteLine($"| Worker Memory Delta | {(memAfter - memBefore) / 1024.0:F1} KiB |");
            _output.WriteLine($"| Worker GC Collections (Gen 0/1/2) | {gc0Delta}/{gc1Delta}/{gc2Delta} |");
            _output.WriteLine($"| Redis Memory (Before -> After) | {redisMemBefore.UsedMemoryHuman} -> {redisMemAfter.UsedMemoryHuman} |");
            _output.WriteLine("-------------------------------------------------------------------------");
            _output.WriteLine("[T3 Benchmark] Completed Successfully.\n");
        }

        // =========================================================================
        // Helper Methods & Diagnostic Instrumentation
        // =========================================================================

        private static async Task ResetEnvironmentAsync(IConnectionMultiplexer redis, IDatabase redisDb)
        {
            var server = redis.GetServer(redis.GetEndPoints()[0]);
            foreach (var key in server.Keys(pattern: "telemetry:archiver:*"))
            {
                await redisDb.KeyDeleteAsync(key);
            }
            await redisDb.KeyDeleteAsync(TelemetryArchiveWorker.StreamKey);
            await redisDb.StringSetAsync(TelemetryArchiveWorker.CheckpointKey, "0-0");
            await redisDb.KeyDeleteAsync(TelemetryArchiveWorker.PendingBatchKey);
        }

        private static List<TelemetryArchivePoint> GenerateTestPoints(int uniqueCount, int duplicateCount)
        {
            var baseTime = DateTimeOffset.UtcNow;
            var list = new List<TelemetryArchivePoint>(uniqueCount + duplicateCount);

            for (int i = 0; i < uniqueCount; i++)
            {
                list.Add(new TelemetryArchivePoint
                {
                    EventId = Guid.NewGuid().ToString("D"),
                    RiderId = $"rider-bench-{100 + (i % 20)}",
                    Timestamp = baseTime.AddMilliseconds(i * 10).ToString("o", CultureInfo.InvariantCulture),
                    Lat = Math.Round(13.750000 + (i * 0.000010), 6),
                    Lng = Math.Round(100.500000 + (i * 0.000010), 6),
                    Accuracy = Math.Round(3.0 + (i % 4) * 0.5, 2)
                });
            }

            // Append duplicates from early items
            for (int d = 0; d < duplicateCount; d++)
            {
                list.Add(list[d % uniqueCount]);
            }

            return list;
        }

        private static NameValueEntry[] ToNameValueEntries(TelemetryArchivePoint p) => new NameValueEntry[]
        {
            new("eventId", p.EventId),
            new("riderId", p.RiderId),
            new("timestamp", p.Timestamp),
            new("lat", p.Lat.ToString("F6", CultureInfo.InvariantCulture)),
            new("lng", p.Lng.ToString("F6", CultureInfo.InvariantCulture)),
            new("accuracy", p.Accuracy.ToString("F2", CultureInfo.InvariantCulture))
        };

        private static async Task<List<TelemetryArchivePoint>> ReadArchivedPointsAsync(ITelemetryArchiveStorage storage, string objectKey)
        {
            byte[] objBytes = Array.Empty<byte>();
            await storage.GetArchiveObjectAsync(objectKey, s =>
            {
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                objBytes = ms.ToArray();
            });

            using var decompressInput = new MemoryStream(objBytes);
            using var gzip = new GZipStream(decompressInput, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            var text = await reader.ReadToEndAsync();

            var lines = text.TrimEnd('\n').Split('\n');
            var points = new List<TelemetryArchivePoint>(lines.Length);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var pt = JsonSerializer.Deserialize<TelemetryArchivePoint>(line);
                if (pt != null) points.Add(pt);
            }
            return points;
        }

        private static async Task<(long UsedMemory, string UsedMemoryHuman)> GetRedisMemoryStatsAsync(IConnectionMultiplexer redis)
        {
            var server = redis.GetServer(redis.GetEndPoints()[0]);
            var info = await server.InfoAsync("memory");
            long usedMem = 0;
            string usedMemHuman = "unknown";

            foreach (var group in info)
            {
                foreach (var kvp in group)
                {
                    if (kvp.Key.Equals("used_memory", StringComparison.OrdinalIgnoreCase))
                    {
                        long.TryParse(kvp.Value, out usedMem);
                    }
                    else if (kvp.Key.Equals("used_memory_human", StringComparison.OrdinalIgnoreCase))
                    {
                        usedMemHuman = kvp.Value;
                    }
                }
            }

            return (usedMem, usedMemHuman);
        }

        public class InstrumentedTelemetryArchiveStorage : ITelemetryArchiveStorage
        {
            private readonly ITelemetryArchiveStorage _inner;
            public int UploadCallCount { get; private set; }
            public long TotalUploadMs { get; private set; }
            public long TotalBytesUploaded { get; private set; }
            public List<long> UploadLatenciesMs { get; } = new();

            public InstrumentedTelemetryArchiveStorage(ITelemetryArchiveStorage inner)
            {
                _inner = inner;
            }

            public string BucketName => _inner.BucketName;

            public Task EnsureArchiveBucketExistsAsync(CancellationToken ct = default) =>
                _inner.EnsureArchiveBucketExistsAsync(ct);

            public async Task<string> PutArchiveObjectAsync(Stream dataStream, string objectKey, string contentType = "application/x-ndjson", CancellationToken ct = default)
            {
                UploadCallCount++;
                var bytes = ((MemoryStream)dataStream).Length;
                TotalBytesUploaded += bytes;

                var sw = Stopwatch.StartNew();
                var result = await _inner.PutArchiveObjectAsync(dataStream, objectKey, contentType, ct);
                sw.Stop();

                UploadLatenciesMs.Add(sw.ElapsedMilliseconds);
                TotalUploadMs += sw.ElapsedMilliseconds;
                return result;
            }

            public Task<bool> ObjectExistsAsync(string objectKey, CancellationToken ct = default) =>
                _inner.ObjectExistsAsync(objectKey, ct);

            public Task GetArchiveObjectAsync(string objectKey, Action<Stream> callback, CancellationToken ct = default) =>
                _inner.GetArchiveObjectAsync(objectKey, callback, ct);
        }
    }
}

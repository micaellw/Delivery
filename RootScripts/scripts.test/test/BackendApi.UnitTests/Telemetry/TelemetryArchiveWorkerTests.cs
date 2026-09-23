using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BackendApi.Services.BackgroundWorkers.Jobs;
using BackendApi.Services.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace BackendApi.UnitTests.Telemetry
{
    public class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public FakeTimeProvider(DateTimeOffset initialTime)
        {
            _utcNow = initialTime;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan span) => _utcNow = _utcNow.Add(span);

        public void SetUtcNow(DateTimeOffset time) => _utcNow = time;
    }

    public class TelemetryArchiveWorkerTests
    {
        private readonly Mock<IConnectionMultiplexer> _redisMock;
        private readonly Mock<IDatabase> _redisDbMock;
        private readonly Mock<ITelemetryArchiveStorage> _archiveStorageMock;
        private readonly Mock<ILogger<TelemetryArchiveWorker>> _loggerMock;
        private readonly FakeTimeProvider _fakeTime;
        private readonly DateTimeOffset _baseTime;
        private readonly HashSet<string> _redisSeenStore;
        private readonly Dictionary<string, string> _redisStringStore;

        public TelemetryArchiveWorkerTests()
        {
            _redisMock = new Mock<IConnectionMultiplexer>();
            _redisDbMock = new Mock<IDatabase>();
            _archiveStorageMock = new Mock<ITelemetryArchiveStorage>();
            _loggerMock = new Mock<ILogger<TelemetryArchiveWorker>>();

            _baseTime = new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero);
            _fakeTime = new FakeTimeProvider(_baseTime);
            _redisSeenStore = new HashSet<string>();
            _redisStringStore = new Dictionary<string, string>();

            _redisMock.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
                .Returns(_redisDbMock.Object);

            // Setup in-memory seen key store on mocked IDatabase
            _redisDbMock
                .Setup(db => db.KeyExistsAsync(It.IsAny<RedisKey>(), CommandFlags.None))
                .ReturnsAsync((RedisKey key, CommandFlags flags) => _redisSeenStore.Contains(key.ToString()!));

            _redisDbMock
                .Setup(db => db.StringSetAsync(
                    It.IsAny<RedisKey>(),
                    It.IsAny<RedisValue>(),
                    It.IsAny<TimeSpan?>(),
                    When.Always,
                    CommandFlags.None))
                .Callback<RedisKey, RedisValue, TimeSpan?, When, CommandFlags>((k, v, exp, w, f) =>
                {
                    _redisSeenStore.Add(k.ToString()!);
                    _redisStringStore[k.ToString()!] = v.ToString()!;
                })
                .ReturnsAsync(true);

            _redisDbMock
                .Setup(db => db.StringGetAsync(It.IsAny<RedisKey>(), CommandFlags.None))
                .ReturnsAsync((RedisKey key, CommandFlags flags) =>
                {
                    if (_redisStringStore.TryGetValue(key.ToString()!, out var val))
                    {
                        return new RedisValue(val);
                    }
                    return RedisValue.Null;
                });

            _redisDbMock
                .Setup(db => db.KeyDeleteAsync(It.IsAny<RedisKey>(), CommandFlags.None))
                .Callback<RedisKey, CommandFlags>((k, f) =>
                {
                    _redisSeenStore.Remove(k.ToString()!);
                    _redisStringStore.Remove(k.ToString()!);
                })
                .ReturnsAsync(true);
        }

        private static StreamEntry[] CreateStreamEntries(int count, long baseTimestampMs = 1726880000000, Guid[]? customEventIds = null)
        {
            var entries = new StreamEntry[count];
            for (int i = 0; i < count; i++)
            {
                var eventId = customEventIds != null && i < customEventIds.Length
                    ? customEventIds[i]
                    : Guid.NewGuid();

                var id = $"{baseTimestampMs + i}-0";
                var values = new NameValueEntry[]
                {
                    new("eventId", eventId.ToString("D")),
                    new("riderId", "rider-123"),
                    new("timestamp", "2026-09-21T00:00:00.0000000Z"),
                    new("lat", "13.7563"),
                    new("lng", "100.5018"),
                    new("accuracy", "5.0")
                };
                entries[i] = new StreamEntry(id, values);
            }
            return entries;
        }

        private static string DecompressGzipToString(byte[] gzipBytes)
        {
            using var input = new MemoryStream(gzipBytes);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        // ==========================================
        // Sub-step 2.2-B.2.1 Tests (Retained)
        // ==========================================

        [Fact]
        public async Task InitializeCheckpointAsync_WhenKeyDoesNotExist_DefaultsTo_0_0()
        {
            _redisDbMock
                .Setup(db => db.StringGetAsync(TelemetryArchiveWorker.CheckpointKey, CommandFlags.None))
                .ReturnsAsync(RedisValue.Null);

            var worker = new TelemetryArchiveWorker(
                _redisMock.Object,
                _archiveStorageMock.Object,
                _loggerMock.Object,
                timeProvider: _fakeTime);

            await worker.InitializeCheckpointAsync();

            Assert.Equal("0-0", worker.CurrentCheckpoint);
        }

        [Fact]
        public async Task InitializeCheckpointAsync_WhenKeyExists_LoadsCheckpoint()
        {
            const string existingCheckpoint = "1789920825336-0";
            _redisDbMock
                .Setup(db => db.StringGetAsync(TelemetryArchiveWorker.CheckpointKey, CommandFlags.None))
                .ReturnsAsync(new RedisValue(existingCheckpoint));

            var worker = new TelemetryArchiveWorker(
                _redisMock.Object,
                _archiveStorageMock.Object,
                _loggerMock.Object,
                timeProvider: _fakeTime);

            await worker.InitializeCheckpointAsync();

            Assert.Equal(existingCheckpoint, worker.CurrentCheckpoint);
        }

        [Fact]
        public async Task ProcessNextBatchAsync_WhenStreamEmpty_ReturnsZero()
        {
            _redisDbMock
                .Setup(db => db.StringGetAsync(TelemetryArchiveWorker.CheckpointKey, CommandFlags.None))
                .ReturnsAsync(new RedisValue("0-0"));

            _redisDbMock
                .Setup(db => db.StreamReadAsync(
                    TelemetryArchiveWorker.StreamKey,
                    new RedisValue("0-0"),
                    5000,
                    CommandFlags.None))
                .ReturnsAsync(Array.Empty<StreamEntry>());

            var worker = new TelemetryArchiveWorker(
                _redisMock.Object,
                _archiveStorageMock.Object,
                _loggerMock.Object,
                timeProvider: _fakeTime);

            await worker.InitializeCheckpointAsync();
            int count = await worker.ProcessNextBatchAsync();

            Assert.Equal(0, count);
            Assert.Equal("0-0", worker.CurrentCheckpoint);
        }

        [Fact]
        public async Task ProcessNextBatchAsync_ReadsExclusivelyGreaterThanCurrentCheckpoint()
        {
            const string initialCheckpoint = "1726880000000-0";
            _redisDbMock
                .Setup(db => db.StringGetAsync(TelemetryArchiveWorker.CheckpointKey, CommandFlags.None))
                .ReturnsAsync(new RedisValue(initialCheckpoint));

            var fakeEntries = CreateStreamEntries(2, 1726880000001);

            _redisDbMock
                .Setup(db => db.StreamReadAsync(
                    TelemetryArchiveWorker.StreamKey,
                    new RedisValue(initialCheckpoint),
                    5000,
                    CommandFlags.None))
                .ReturnsAsync(fakeEntries);

            var worker = new TelemetryArchiveWorker(
                _redisMock.Object,
                _archiveStorageMock.Object,
                _loggerMock.Object,
                timeProvider: _fakeTime);

            await worker.InitializeCheckpointAsync();

            int count = await worker.ProcessNextBatchAsync();

            Assert.Equal(2, worker.BufferedCount);
            _redisDbMock.Verify(db => db.StreamReadAsync(
                TelemetryArchiveWorker.StreamKey,
                new RedisValue(initialCheckpoint),
                5000,
                CommandFlags.None), Times.Once);
        }

        [Fact]
        public async Task ProcessNextBatchAsync_WhenRedisUnavailable_ReturnsZeroWithoutThrowing()
        {
            var nullRedisMock = new Mock<IConnectionMultiplexer>();
            nullRedisMock.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns((IDatabase)null!);

            var worker = new TelemetryArchiveWorker(
                nullRedisMock.Object,
                _archiveStorageMock.Object,
                _loggerMock.Object,
                timeProvider: _fakeTime);

            int count = await worker.ProcessNextBatchAsync();

            Assert.Equal(0, count);
        }

        [Fact]
        public void DependencyInjection_Resolves_TelemetryArchiveWorker_AsHostedService()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(_redisMock.Object);
            services.AddSingleton(_archiveStorageMock.Object);
            services.AddHostedService<TelemetryArchiveWorker>();

            var provider = services.BuildServiceProvider();
            var hostedServices = provider.GetServices<IHostedService>();

            Assert.Contains(hostedServices, s => s is TelemetryArchiveWorker);
        }

        // ==========================================
        // Sub-step 2.2-B.2.2 Batch Collection Tests (Retained)
        // ==========================================

        [Fact]
        public async Task CollectBatchAsync_WhenStreamEmpty_DoesNotCreateBatch()
        {
            _redisDbMock
                .Setup(db => db.StreamReadAsync(
                    TelemetryArchiveWorker.StreamKey,
                    It.IsAny<RedisValue>(),
                    It.IsAny<int?>(),
                    CommandFlags.None))
                .ReturnsAsync(Array.Empty<StreamEntry>());

            var worker = new TelemetryArchiveWorker(
                _redisMock.Object,
                _archiveStorageMock.Object,
                _loggerMock.Object,
                timeProvider: _fakeTime);

            var batch = await worker.CollectBatchAsync();

            Assert.Null(batch);
            Assert.Equal(0, worker.BufferedCount);
        }

        [Fact]
        public async Task CollectBatchAsync_With100Entries_FlushesAfter60sTimeoutWithCorrectBoundaries()
        {
            var entries = CreateStreamEntries(100, 1789920825000);
            _redisDbMock
                .Setup(db => db.StreamReadAsync(
                    TelemetryArchiveWorker.StreamKey,
                    It.IsAny<RedisValue>(),
                    It.IsAny<int?>(),
                    CommandFlags.None))
                .ReturnsAsync(entries);

            var worker = new TelemetryArchiveWorker(
                _redisMock.Object,
                _archiveStorageMock.Object,
                _loggerMock.Object,
                timeProvider: _fakeTime);

            // Step 1: Read 100 entries. Buffer count = 100, timer starts, batch is null (wait interval not met).
            var batch1 = await worker.CollectBatchAsync();
            Assert.Null(batch1);
            Assert.Equal(100, worker.BufferedCount);

            // Step 2: Simulate 60 seconds passing.
            _fakeTime.Advance(TimeSpan.FromSeconds(60));

            // Stream now empty on subsequent read
            _redisDbMock
                .Setup(db => db.StreamReadAsync(
                    TelemetryArchiveWorker.StreamKey,
                    It.IsAny<RedisValue>(),
                    It.IsAny<int?>(),
                    CommandFlags.None))
                .ReturnsAsync(Array.Empty<StreamEntry>());

            // Step 3: Collect again -> Rule 2 triggers flush
            var batch2 = await worker.CollectBatchAsync();
            Assert.NotNull(batch2);
            Assert.Equal(100, batch2.RawEntries.Count);
            Assert.Equal(entries[0].Id.ToString(), batch2.FirstStreamId);
            Assert.Equal(entries[99].Id.ToString(), batch2.LastStreamId);
            Assert.Equal(0, worker.BufferedCount);
        }

        [Fact]
        public async Task CollectBatchAsync_With4999Entries_WaitsAsPartialBatch()
        {
            var entries = CreateStreamEntries(4999, 1789920825000);
            _redisDbMock
                .Setup(db => db.StreamReadAsync(
                    TelemetryArchiveWorker.StreamKey,
                    It.IsAny<RedisValue>(),
                    It.IsAny<int?>(),
                    CommandFlags.None))
                .ReturnsAsync(entries);

            var worker = new TelemetryArchiveWorker(
                _redisMock.Object,
                _archiveStorageMock.Object,
                _loggerMock.Object,
                timeProvider: _fakeTime);

            // Advance only 30s (< 60s timeout)
            var batch = await worker.CollectBatchAsync();
            _fakeTime.Advance(TimeSpan.FromSeconds(30));

            Assert.Null(batch);
            Assert.Equal(4999, worker.BufferedCount);
        }

        [Fact]
        public async Task CollectBatchAsync_With5000Entries_FlushesImmediatelyWithoutWaitingForTimer()
        {
            var entries = CreateStreamEntries(5000, 1789920825000);
            _redisDbMock
                .Setup(db => db.StreamReadAsync(
                    TelemetryArchiveWorker.StreamKey,
                    It.IsAny<RedisValue>(),
                    It.IsAny<int?>(),
                    CommandFlags.None))
                .ReturnsAsync(entries);

            var worker = new TelemetryArchiveWorker(
                _redisMock.Object,
                _archiveStorageMock.Object,
                _loggerMock.Object,
                timeProvider: _fakeTime);

            // Flushes immediately on Rule 1 (Batch size threshold reached)
            var batch = await worker.CollectBatchAsync();

            Assert.NotNull(batch);
            Assert.Equal(5000, batch.RawEntries.Count);
            Assert.Equal(entries[0].Id.ToString(), batch.FirstStreamId);
            Assert.Equal(entries[4999].Id.ToString(), batch.LastStreamId);
            Assert.Equal(0, worker.BufferedCount);
        }

        [Fact]
        public async Task CollectBatchAsync_WhenMoreThan5000Available_CapsReadRequestAtNeededCount()
        {
            var initial1000 = CreateStreamEntries(1000, 1789920825000);
            _redisDbMock
                .Setup(db => db.StreamReadAsync(
                    TelemetryArchiveWorker.StreamKey,
                    It.IsAny<RedisValue>(),
                    5000,
                    CommandFlags.None))
                .ReturnsAsync(initial1000);

            var worker = new TelemetryArchiveWorker(
                _redisMock.Object,
                _archiveStorageMock.Object,
                _loggerMock.Object,
                timeProvider: _fakeTime);

            await worker.CollectBatchAsync();
            Assert.Equal(1000, worker.BufferedCount);

            // Second read must ask strictly for 5000 - 1000 = 4000
            var next4000 = CreateStreamEntries(4000, 1789920826000);
            _redisDbMock
                .Setup(db => db.StreamReadAsync(
                    TelemetryArchiveWorker.StreamKey,
                    initial1000[999].Id,
                    4000,
                    CommandFlags.None))
                .ReturnsAsync(next4000);

            var batch = await worker.CollectBatchAsync();

            Assert.NotNull(batch);
            Assert.Equal(5000, batch.RawEntries.Count);
            Assert.Equal(initial1000[0].Id.ToString(), batch.FirstStreamId);
            Assert.Equal(next4000[3999].Id.ToString(), batch.LastStreamId);
        }

        [Fact]
        public void BuildObjectKey_GeneratesDeterministicFormat()
        {
            var timestamp = new DateTimeOffset(2026, 9, 21, 0, 15, 30, TimeSpan.Zero);
            const string firstId = "1789920825336-0";
            const string lastId = "1789920825401-7";

            var objectKey = TelemetryArchiveWorker.BuildObjectKey(timestamp, firstId, lastId);

            const string expected = "gps/year=2026/month=09/day=21/hour=00/batch_1789920825336-0_1789920825401-7.ndjson.gz";
            Assert.Equal(expected, objectKey);
        }

        [Fact]
        public void PendingBatchMetadata_FormatsAndParsesCorrectly()
        {
            const string firstId = "1789920825336-0";
            const string lastId = "1789920825401-7";

            var formatted = TelemetryArchiveWorker.FormatPendingBatch(firstId, lastId);
            var parsed = TelemetryArchiveWorker.ParsePendingBatch(formatted);

            Assert.Equal("1789920825336-0|1789920825401-7", formatted);
            Assert.NotNull(parsed);
            Assert.Equal(firstId, parsed.Value.firstStreamId);
            Assert.Equal(lastId, parsed.Value.lastStreamId);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("invalid_payload")]
        [InlineData("first|")]
        [InlineData("|last")]
        [InlineData("first|middle|last")]
        public void ParsePendingBatch_WithInvalidPayload_ReturnsNull(string? payload)
        {
            var parsed = TelemetryArchiveWorker.ParsePendingBatch(payload);
            Assert.Null(parsed);
        }

        // ==========================================
        // Sub-step 2.2-B.2.3 Deduplication Tests (Retained)
        // ==========================================

        [Fact]
        public async Task Test1_NoDuplicates_RetainsAllEntriesAndPreservesBoundaries()
        {
            var entries = CreateStreamEntries(10, 1789920825000);
            _redisDbMock
                .Setup(db => db.StreamReadAsync(
                    TelemetryArchiveWorker.StreamKey,
                    It.IsAny<RedisValue>(),
                    It.IsAny<int?>(),
                    CommandFlags.None))
                .ReturnsAsync(entries);

            var worker = new TelemetryArchiveWorker(
                _redisMock.Object,
                _archiveStorageMock.Object,
                _loggerMock.Object,
                timeProvider: _fakeTime);

            await worker.CollectBatchAsync();
            _fakeTime.Advance(TimeSpan.FromSeconds(60));

            var batch = await worker.CollectBatchAsync();

            Assert.NotNull(batch);
            Assert.Equal(10, batch.RawEntries.Count);
            Assert.Equal(10, batch.DeduplicatedEntries.Count);
            Assert.Equal(10, batch.Entries.Count);
            Assert.Equal(entries[0].Id.ToString(), batch.FirstStreamId);
            Assert.Equal(entries[9].Id.ToString(), batch.LastStreamId);
        }

        [Fact]
        public async Task Test2_InBatchDuplicate_FiltersDuplicatePayload_AndRetainsExactBatchBoundaries()
        {
            var duplicateGuid = Guid.NewGuid();
            var eventIds = new Guid[10];
            for (int i = 0; i < 10; i++)
            {
                eventIds[i] = (i == 2 || i == 6) ? duplicateGuid : Guid.NewGuid();
            }

            var entries = CreateStreamEntries(10, 1789920825000, eventIds);
            _redisDbMock
                .Setup(db => db.StreamReadAsync(
                    TelemetryArchiveWorker.StreamKey,
                    It.IsAny<RedisValue>(),
                    It.IsAny<int?>(),
                    CommandFlags.None))
                .ReturnsAsync(entries);

            var worker = new TelemetryArchiveWorker(
                _redisMock.Object,
                _archiveStorageMock.Object,
                _loggerMock.Object,
                timeProvider: _fakeTime);

            await worker.CollectBatchAsync();
            _fakeTime.Advance(TimeSpan.FromSeconds(60));

            var batch = await worker.CollectBatchAsync();

            Assert.NotNull(batch);
            Assert.Equal(10, batch.RawEntries.Count);
            Assert.Equal(9, batch.DeduplicatedEntries.Count);
            Assert.Equal(entries[0].Id.ToString(), batch.FirstStreamId);
            Assert.Equal(entries[9].Id.ToString(), batch.LastStreamId);
        }

        [Fact]
        public async Task Test3_CrossBatchDuplicate_FiltersSeenEvent_AndAdvancesCheckpointBoundaryToLastEntry()
        {
            var eventA = Guid.NewGuid();
            var eventB = Guid.NewGuid();
            var eventC = Guid.NewGuid();

            var batch1Entries = CreateStreamEntries(1, 1789920820000, new[] { eventA });
            var batch2Entries = CreateStreamEntries(3, 1789920825000, new[] { eventA, eventB, eventC });

            _redisDbMock
                .SetupSequence(db => db.StreamReadAsync(
                    TelemetryArchiveWorker.StreamKey,
                    It.IsAny<RedisValue>(),
                    It.IsAny<int?>(),
                    CommandFlags.None))
                .ReturnsAsync(batch1Entries)
                .ReturnsAsync(batch2Entries);

            var worker = new TelemetryArchiveWorker(
                _redisMock.Object,
                _archiveStorageMock.Object,
                _loggerMock.Object,
                timeProvider: _fakeTime);

            await worker.CollectBatchAsync();
            _fakeTime.Advance(TimeSpan.FromSeconds(60));
            var batch1 = await worker.CollectBatchAsync();
            Assert.NotNull(batch1);
            Assert.Single(batch1.DeduplicatedEntries);

            var seenKeyA = TelemetryArchiveWorker.SeenPrefix + eventA.ToString("D");
            Assert.Contains(seenKeyA, _redisSeenStore);

            await worker.CollectBatchAsync();
            _fakeTime.Advance(TimeSpan.FromSeconds(60));
            var batch2 = await worker.CollectBatchAsync();

            Assert.NotNull(batch2);
            Assert.Equal(3, batch2.RawEntries.Count);
            Assert.Equal(2, batch2.DeduplicatedEntries.Count);
            Assert.Equal(batch2Entries[0].Id.ToString(), batch2.FirstStreamId);
            Assert.Equal(batch2Entries[2].Id.ToString(), batch2.LastStreamId);
        }

        [Fact]
        public async Task Test4_MissingSeenKey_DoesNotFilterEvent_PassesIntoArchivePipeline()
        {
            var eventX = Guid.NewGuid();
            var entries = CreateStreamEntries(1, 1789920825000, new[] { eventX });

            var worker = new TelemetryArchiveWorker(
                _redisMock.Object,
                _archiveStorageMock.Object,
                _loggerMock.Object,
                timeProvider: _fakeTime);

            var unique = await worker.DeduplicateEntriesAsync(entries, _redisDbMock.Object);

            Assert.Single(unique);
            var seenKeyX = TelemetryArchiveWorker.SeenPrefix + eventX.ToString("D");
            Assert.Contains(seenKeyX, _redisSeenStore);
        }

        [Fact]
        public async Task Test5_TtlExpiration_AllowsEventToReEnterPipelineAfterSeenKeyExpires()
        {
            var eventX = Guid.NewGuid();
            var entries = CreateStreamEntries(1, 1789920825000, new[] { eventX });

            var worker = new TelemetryArchiveWorker(
                _redisMock.Object,
                _archiveStorageMock.Object,
                _loggerMock.Object,
                timeProvider: _fakeTime);

            var firstPass = await worker.DeduplicateEntriesAsync(entries, _redisDbMock.Object);
            Assert.Single(firstPass);

            var seenKeyX = TelemetryArchiveWorker.SeenPrefix + eventX.ToString("D");
            Assert.Contains(seenKeyX, _redisSeenStore);

            var duplicatePass = await worker.DeduplicateEntriesAsync(entries, _redisDbMock.Object);
            Assert.Empty(duplicatePass);

            _redisSeenStore.Remove(seenKeyX);

            var expiredPass = await worker.DeduplicateEntriesAsync(entries, _redisDbMock.Object);

            Assert.Single(expiredPass);
        }

        [Fact]
        public void ExtractEventId_ExtractsValidGuidAndHandlesMissingGracefully()
        {
            var expectedGuid = Guid.NewGuid();
            var validEntry = new StreamEntry("1-0", new NameValueEntry[]
            {
                new("eventId", expectedGuid.ToString("D")),
                new("riderId", "r1")
            });

            var missingEntry = new StreamEntry("2-0", new NameValueEntry[]
            {
                new("riderId", "r1")
            });

            var invalidEntry = new StreamEntry("3-0", new NameValueEntry[]
            {
                new("eventId", "not-a-valid-guid")
            });

            Assert.Equal(expectedGuid, TelemetryArchiveWorker.ExtractEventId(validEntry));
            Assert.Null(TelemetryArchiveWorker.ExtractEventId(missingEntry));
            Assert.Null(TelemetryArchiveWorker.ExtractEventId(invalidEntry));
        }

        // ==========================================
        // Sub-step 2.2-B.2.4 NDJSON.GZ + MinIO Tests (Retained)
        // ==========================================

        [Fact]
        public void SerializeToNdjson_WithEmptyEntries_ReturnsEmptyBytes()
        {
            var bytes = TelemetryArchiveWorker.SerializeToNdjson(Array.Empty<StreamEntry>());
            Assert.Empty(bytes);

            var compressed = TelemetryArchiveWorker.CompressGzip(bytes);
            Assert.NotEmpty(compressed); // Valid GZip header + footer (~20 bytes)

            var decompressed = DecompressGzipToString(compressed);
            Assert.Empty(decompressed);
        }

        [Fact]
        public void SerializeToNdjson_WithSingleEntry_ProducesValidJsonWithNewline()
        {
            var eventId = Guid.NewGuid();
            var entries = CreateStreamEntries(1, 1789920825000, new[] { eventId });

            var bytes = TelemetryArchiveWorker.SerializeToNdjson(entries);
            var ndjsonText = Encoding.UTF8.GetString(bytes);

            Assert.EndsWith("\n", ndjsonText);
            var lines = ndjsonText.TrimEnd('\n').Split('\n');
            Assert.Single(lines);

            using var doc = JsonDocument.Parse(lines[0]);
            var root = doc.RootElement;
            Assert.Equal(eventId.ToString("D"), root.GetProperty("eventId").GetString());
            Assert.Equal("rider-123", root.GetProperty("riderId").GetString());
            Assert.Equal("2026-09-21T00:00:00.0000000Z", root.GetProperty("timestamp").GetString());
            Assert.Equal(13.7563, root.GetProperty("lat").GetDouble());
            Assert.Equal(100.5018, root.GetProperty("lng").GetDouble());
            Assert.Equal(5.0, root.GetProperty("accuracy").GetDouble());
        }

        [Fact]
        public void SerializeToNdjson_WithMultipleEntries_ProducesExactLineCountAndSchema()
        {
            var entries = CreateStreamEntries(5, 1789920825000);

            var bytes = TelemetryArchiveWorker.SerializeToNdjson(entries);
            var ndjsonText = Encoding.UTF8.GetString(bytes);

            var lines = ndjsonText.TrimEnd('\n').Split('\n');
            Assert.Equal(5, lines.Length);

            for (int i = 0; i < 5; i++)
            {
                using var doc = JsonDocument.Parse(lines[i]);
                var root = doc.RootElement;
                Assert.True(root.TryGetProperty("eventId", out _));
                Assert.True(root.TryGetProperty("riderId", out _));
                Assert.True(root.TryGetProperty("timestamp", out _));
                Assert.True(root.TryGetProperty("lat", out _));
                Assert.True(root.TryGetProperty("lng", out _));
                Assert.True(root.TryGetProperty("accuracy", out _));
            }
        }

        [Fact]
        public void Gzip_RoundTrip_CompressesAndDecompressesAccurately()
        {
            var entries = CreateStreamEntries(10, 1789920825000);

            var rawBytes = TelemetryArchiveWorker.SerializeToNdjson(entries);
            var rawText = Encoding.UTF8.GetString(rawBytes);

            var gzipBytes = TelemetryArchiveWorker.CompressGzip(rawBytes);
            Assert.True(gzipBytes.Length < rawBytes.Length); // Compression achieved

            var decompressedText = DecompressGzipToString(gzipBytes);
            Assert.Equal(rawText, decompressedText);
        }

        [Fact]
        public async Task UploadBatchAsync_CallsStorageWithCorrectObjectKeyAndContentType()
        {
            var entries = CreateStreamEntries(3, 1789920825000);
            var timestamp = new DateTimeOffset(2026, 9, 21, 1, 0, 0, TimeSpan.Zero);
            var batch = new TelemetryBatch(entries[0].Id.ToString(), entries[2].Id.ToString(), entries, entries, timestamp);

            string capturedKey = string.Empty;
            string capturedContentType = string.Empty;
            byte[] capturedData = Array.Empty<byte>();

            _archiveStorageMock
                .Setup(s => s.PutArchiveObjectAsync(
                    It.IsAny<Stream>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .Callback<Stream, string, string, CancellationToken>((stream, key, cType, ct) =>
                {
                    capturedKey = key;
                    capturedContentType = cType;
                    using var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    capturedData = ms.ToArray();
                })
                .ReturnsAsync((Stream s, string k, string ct, CancellationToken c) => k);

            var worker = new TelemetryArchiveWorker(
                _redisMock.Object,
                _archiveStorageMock.Object,
                _loggerMock.Object,
                timeProvider: _fakeTime);

            // Act
            var uploadedKey = await worker.UploadBatchAsync(batch);

            // Assert
            var expectedKey = batch.GenerateObjectKey();
            Assert.Equal(expectedKey, uploadedKey);
            Assert.Equal(expectedKey, capturedKey);
            Assert.Equal("application/gzip", capturedContentType);

            // Verify payload inside decompresses to 3 NDJSON lines
            var decompressed = DecompressGzipToString(capturedData);
            var lines = decompressed.TrimEnd('\n').Split('\n');
            Assert.Equal(3, lines.Length);
        }

        [Fact]
        public async Task UploadBatchAsync_PreservesBoundaryKeyFromRawEntries_WhilePayloadContainsDeduplicatedEntries()
        {
            // Raw count = 10, Deduplicated count = 9
            var duplicateGuid = Guid.NewGuid();
            var eventIds = new Guid[10];
            for (int i = 0; i < 10; i++)
            {
                eventIds[i] = (i == 1 || i == 5) ? duplicateGuid : Guid.NewGuid();
            }

            var rawEntries = CreateStreamEntries(10, 1789920825000, eventIds);
            var timestamp = new DateTimeOffset(2026, 9, 21, 1, 0, 0, TimeSpan.Zero);

            var worker = new TelemetryArchiveWorker(
                _redisMock.Object,
                _archiveStorageMock.Object,
                _loggerMock.Object,
                timeProvider: _fakeTime);

            var deduplicated = await worker.DeduplicateEntriesAsync(rawEntries, _redisDbMock.Object);
            Assert.Equal(9, deduplicated.Count);

            var batch = new TelemetryBatch(rawEntries[0].Id.ToString(), rawEntries[9].Id.ToString(), rawEntries, deduplicated, timestamp);

            string capturedKey = string.Empty;
            byte[] capturedData = Array.Empty<byte>();

            _archiveStorageMock
                .Setup(s => s.PutArchiveObjectAsync(
                    It.IsAny<Stream>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .Callback<Stream, string, string, CancellationToken>((stream, key, cType, ct) =>
                {
                    capturedKey = key;
                    using var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    capturedData = ms.ToArray();
                })
                .ReturnsAsync((Stream s, string k, string ct, CancellationToken c) => k);

            // Act
            await worker.UploadBatchAsync(batch);

            // Assert: Key strictly uses raw[0].Id and raw[9].Id
            Assert.Contains($"batch_{rawEntries[0].Id}_{rawEntries[9].Id}.ndjson.gz", capturedKey);

            // Assert: Payload strictly contains 9 records (deduplicated)
            var decompressed = DecompressGzipToString(capturedData);
            var lines = decompressed.TrimEnd('\n').Split('\n');
            Assert.Equal(9, lines.Length);
        }

        [Fact]
        public async Task UploadBatchAsync_WhenStorageThrowsException_PropagatesExceptionUpwards()
        {
            var entries = CreateStreamEntries(2, 1789920825000);
            var batch = new TelemetryBatch(entries[0].Id.ToString(), entries[1].Id.ToString(), entries, entries, DateTimeOffset.UtcNow);

            _archiveStorageMock
                .Setup(s => s.PutArchiveObjectAsync(
                    It.IsAny<Stream>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("MinIO is unreachable"));

            var worker = new TelemetryArchiveWorker(
                _redisMock.Object,
                _archiveStorageMock.Object,
                _loggerMock.Object,
                timeProvider: _fakeTime);

            // Act & Assert: Exception propagates, checkpoint not advanced
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => worker.UploadBatchAsync(batch));
            Assert.Equal("MinIO is unreachable", ex.Message);
            Assert.Equal("0-0", worker.CurrentCheckpoint); // Checkpoint strictly intact
        }

        // ==========================================
        // Sub-step 2.2-B.2.5 Pending Batch & Crash Recovery Tests
        // ==========================================

        [Fact]
        public async Task ProcessBatchPipelineAsync_StagesPendingBatch_BeforeStorageUpload()
        {
            var entries = CreateStreamEntries(2, 1789920825000);
            var batch = new TelemetryBatch(entries[0].Id.ToString(), entries[1].Id.ToString(), entries, entries, DateTimeOffset.UtcNow);

            var callSequence = new List<string>();

            _redisDbMock
                .Setup(db => db.StringSetAsync(
                    TelemetryArchiveWorker.PendingBatchKey,
                    It.IsAny<RedisValue>(),
                    It.IsAny<TimeSpan?>(),
                    When.Always,
                    CommandFlags.None))
                .Callback<RedisKey, RedisValue, TimeSpan?, When, CommandFlags>((k, v, e, w, f) => callSequence.Add("SET_PENDING"))
                .ReturnsAsync(true);

            _archiveStorageMock
                .Setup(s => s.PutArchiveObjectAsync(
                    It.IsAny<Stream>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .Callback<Stream, string, string, CancellationToken>((st, k, c, ct) => callSequence.Add("PUT_ARCHIVE"))
                .ReturnsAsync("key");

            var worker = new TelemetryArchiveWorker(
                _redisMock.Object,
                _archiveStorageMock.Object,
                _loggerMock.Object,
                timeProvider: _fakeTime);

            await worker.ProcessBatchPipelineAsync(batch);

            Assert.Equal(2, callSequence.Count);
            Assert.Equal("SET_PENDING", callSequence[0]);
            Assert.Equal("PUT_ARCHIVE", callSequence[1]);
        }

        [Fact]
        public async Task ProcessBatchPipelineAsync_WhenUploadSucceeds_AdvancesCheckpointAndDeletesPendingBatch()
        {
            var entries = CreateStreamEntries(3, 1789920825000);
            var batch = new TelemetryBatch(entries[0].Id.ToString(), entries[2].Id.ToString(), entries, entries, DateTimeOffset.UtcNow);

            _archiveStorageMock
                .Setup(s => s.PutArchiveObjectAsync(
                    It.IsAny<Stream>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync("uploaded-key");

            var worker = new TelemetryArchiveWorker(
                _redisMock.Object,
                _archiveStorageMock.Object,
                _loggerMock.Object,
                timeProvider: _fakeTime);

            await worker.ProcessBatchPipelineAsync(batch);

            // Verify Checkpoint advanced to lastStreamId
            Assert.Equal(batch.LastStreamId, worker.CurrentCheckpoint);
            _redisDbMock.Verify(db => db.StringSetAsync(
                TelemetryArchiveWorker.CheckpointKey,
                batch.LastStreamId,
                null,
                When.Always,
                CommandFlags.None), Times.Once);

            // Verify Pending Batch staging key deleted
            _redisDbMock.Verify(db => db.KeyDeleteAsync(
                TelemetryArchiveWorker.PendingBatchKey,
                CommandFlags.None), Times.Once);
        }

        [Fact]
        public async Task ProcessBatchPipelineAsync_WhenUploadFails_CheckpointNotAdvanced_AndPendingBatchRemains()
        {
            var entries = CreateStreamEntries(2, 1789920825000);
            var batch = new TelemetryBatch(entries[0].Id.ToString(), entries[1].Id.ToString(), entries, entries, DateTimeOffset.UtcNow);

            _archiveStorageMock
                .Setup(s => s.PutArchiveObjectAsync(
                    It.IsAny<Stream>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Upload to MinIO failed"));

            var worker = new TelemetryArchiveWorker(
                _redisMock.Object,
                _archiveStorageMock.Object,
                _loggerMock.Object,
                timeProvider: _fakeTime);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => worker.ProcessBatchPipelineAsync(batch));
            Assert.Equal("Upload to MinIO failed", ex.Message);

            // Checkpoint must NOT advance
            Assert.Equal("0-0", worker.CurrentCheckpoint);
            _redisDbMock.Verify(db => db.StringSetAsync(
                TelemetryArchiveWorker.CheckpointKey,
                It.IsAny<RedisValue>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()), Times.Never);

            // Pending staging key must NOT be deleted (so recovery can resume it)
            _redisDbMock.Verify(db => db.KeyDeleteAsync(
                TelemetryArchiveWorker.PendingBatchKey,
                CommandFlags.None), Times.Never);
        }

        [Fact]
        public async Task RecoverPendingBatchAsync_CaseA_WhenObjectNotInMinIO_ArchivesStreamRangeAndAdvancesCheckpoint()
        {
            // Case A: Crash occurred before upload finished. Pending batch exists, object does NOT exist in MinIO.
            const string firstId = "1789920825000-0";
            const string lastId = "1789920825000-9";
            _redisStringStore[TelemetryArchiveWorker.PendingBatchKey] = $"{firstId}|{lastId}";

            var entries = CreateStreamEntries(10, 1789920825000);

            _archiveStorageMock
                .Setup(s => s.ObjectExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);

            _redisDbMock
                .Setup(db => db.StreamRangeAsync(
                    TelemetryArchiveWorker.StreamKey,
                    firstId,
                    lastId,
                    null,
                    Order.Ascending,
                    CommandFlags.None))
                .ReturnsAsync(entries);

            _archiveStorageMock
                .Setup(s => s.PutArchiveObjectAsync(
                    It.IsAny<Stream>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync("recovered-key");

            var worker = new TelemetryArchiveWorker(
                _redisMock.Object,
                _archiveStorageMock.Object,
                _loggerMock.Object,
                timeProvider: _fakeTime);

            bool recovered = await worker.RecoverPendingBatchAsync();

            Assert.True(recovered);
            Assert.Equal(lastId, worker.CurrentCheckpoint);

            _archiveStorageMock.Verify(s => s.PutArchiveObjectAsync(
                It.IsAny<Stream>(),
                It.Is<string>(k => k.Contains($"batch_{firstId}_{lastId}.ndjson.gz")),
                "application/gzip",
                It.IsAny<CancellationToken>()), Times.Once);

            _redisDbMock.Verify(db => db.StringSetAsync(
                TelemetryArchiveWorker.CheckpointKey,
                lastId,
                null,
                When.Always,
                CommandFlags.None), Times.Once);

            _redisDbMock.Verify(db => db.KeyDeleteAsync(
                TelemetryArchiveWorker.PendingBatchKey,
                CommandFlags.None), Times.Once);
        }

        [Fact]
        public async Task RecoverPendingBatchAsync_CaseB_WhenObjectAlreadyInMinIO_AdvancesCheckpointWithoutReUpload()
        {
            // Case B: MinIO upload succeeded before crash, but checkpoint was not yet committed.
            const string firstId = "1789920825000-0";
            const string lastId = "1789920825000-9";
            _redisStringStore[TelemetryArchiveWorker.PendingBatchKey] = $"{firstId}|{lastId}";

            _archiveStorageMock
                .Setup(s => s.ObjectExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true); // Object exists in MinIO!

            var worker = new TelemetryArchiveWorker(
                _redisMock.Object,
                _archiveStorageMock.Object,
                _loggerMock.Object,
                timeProvider: _fakeTime);

            bool recovered = await worker.RecoverPendingBatchAsync();

            Assert.True(recovered);
            Assert.Equal(lastId, worker.CurrentCheckpoint);

            // Re-upload must NOT occur
            _archiveStorageMock.Verify(s => s.PutArchiveObjectAsync(
                It.IsAny<Stream>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()), Times.Never);

            // Checkpoint must advance
            _redisDbMock.Verify(db => db.StringSetAsync(
                TelemetryArchiveWorker.CheckpointKey,
                lastId,
                null,
                When.Always,
                CommandFlags.None), Times.Once);

            // Pending staging key must be cleaned
            _redisDbMock.Verify(db => db.KeyDeleteAsync(
                TelemetryArchiveWorker.PendingBatchKey,
                CommandFlags.None), Times.Once);
        }

        [Fact]
        public async Task RecoverPendingBatchAsync_CaseC_WhenCheckpointAlreadyAtOrPastLastStreamId_ClearsPendingWithoutReUpload()
        {
            // Case C: Checkpoint was committed, but crash occurred before DEL pending_batch.
            const string firstId = "1789920825000-0";
            const string lastId = "1789920825000-9";
            _redisStringStore[TelemetryArchiveWorker.PendingBatchKey] = $"{firstId}|{lastId}";
            _redisStringStore[TelemetryArchiveWorker.CheckpointKey] = lastId; // Checkpoint already at lastId!

            var worker = new TelemetryArchiveWorker(
                _redisMock.Object,
                _archiveStorageMock.Object,
                _loggerMock.Object,
                timeProvider: _fakeTime);

            await worker.InitializeCheckpointAsync();

            Assert.Equal(lastId, worker.CurrentCheckpoint);

            // Must NOT re-upload or touch MinIO
            _archiveStorageMock.Verify(s => s.PutArchiveObjectAsync(
                It.IsAny<Stream>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()), Times.Never);

            // Must delete pending key
            _redisDbMock.Verify(db => db.KeyDeleteAsync(
                TelemetryArchiveWorker.PendingBatchKey,
                CommandFlags.None), Times.Once);
        }

        [Fact]
        public async Task RecoverPendingBatchAsync_WhenStreamIntact_SuccessfullyRecoversBatch()
        {
            const string firstId = "1789920825000-0";
            const string lastId = "1789920825000-3";
            _redisStringStore[TelemetryArchiveWorker.PendingBatchKey] = $"{firstId}|{lastId}";

            var entries = CreateStreamEntries(4, 1789920825000);

            _archiveStorageMock
                .Setup(s => s.ObjectExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);

            _redisDbMock
                .Setup(db => db.StreamRangeAsync(
                    TelemetryArchiveWorker.StreamKey,
                    firstId,
                    lastId,
                    null,
                    Order.Ascending,
                    CommandFlags.None))
                .ReturnsAsync(entries);

            _archiveStorageMock
                .Setup(s => s.PutArchiveObjectAsync(
                    It.IsAny<Stream>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync("recovered-key");

            var worker = new TelemetryArchiveWorker(
                _redisMock.Object,
                _archiveStorageMock.Object,
                _loggerMock.Object,
                timeProvider: _fakeTime);

            bool recovered = await worker.RecoverPendingBatchAsync();

            Assert.True(recovered);
            Assert.Equal(lastId, worker.CurrentCheckpoint);
            Assert.False(worker.IsHalted);
        }

        [Fact]
        public async Task RecoverPendingBatchAsync_WhenStreamTrimGapDetected_LogsCriticalAndEntersSafeHalt()
        {
            // Start entry 'firstId' was pruned by XTRIM
            const string firstId = "1789920825000-0";
            const string lastId = "1789920825000-9";
            _redisStringStore[TelemetryArchiveWorker.PendingBatchKey] = $"{firstId}|{lastId}";

            // Stream returns entries starting at index 3 instead of index 0
            var trimmedEntries = CreateStreamEntries(7, 1789920825003);

            _archiveStorageMock
                .Setup(s => s.ObjectExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);

            _redisDbMock
                .Setup(db => db.StreamRangeAsync(
                    TelemetryArchiveWorker.StreamKey,
                    firstId,
                    lastId,
                    null,
                    Order.Ascending,
                    CommandFlags.None))
                .ReturnsAsync(trimmedEntries);

            var worker = new TelemetryArchiveWorker(
                _redisMock.Object,
                _archiveStorageMock.Object,
                _loggerMock.Object,
                timeProvider: _fakeTime);

            var ex = await Assert.ThrowsAsync<TelemetryStreamTrimGapException>(() => worker.RecoverPendingBatchAsync());
            Assert.Contains(firstId, ex.Message);
            Assert.True(worker.IsHalted);
            Assert.Equal("0-0", worker.CurrentCheckpoint); // Checkpoint untouched
        }

        [Fact]
        public async Task InitializeCheckpointAsync_WhenNoPendingBatch_StartsNormallyWithoutRecovery()
        {
            const string existingCheckpoint = "1789920825000-5";
            _redisStringStore[TelemetryArchiveWorker.CheckpointKey] = existingCheckpoint;

            var worker = new TelemetryArchiveWorker(
                _redisMock.Object,
                _archiveStorageMock.Object,
                _loggerMock.Object,
                timeProvider: _fakeTime);

            await worker.InitializeCheckpointAsync();

            Assert.Equal(existingCheckpoint, worker.CurrentCheckpoint);
            Assert.False(worker.IsHalted);
            _archiveStorageMock.Verify(s => s.ObjectExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task InitializeCheckpointAsync_WhenInitial_0_0_DoesNotTreatAsTrimGap()
        {
            // Initial clean start: checkpoint is 0-0, no pending batch
            var worker = new TelemetryArchiveWorker(
                _redisMock.Object,
                _archiveStorageMock.Object,
                _loggerMock.Object,
                timeProvider: _fakeTime);

            await worker.InitializeCheckpointAsync();

            Assert.Equal("0-0", worker.CurrentCheckpoint);
            Assert.False(worker.IsHalted);
        }

        [Theory]
        [InlineData("0-0", "0-0", 0)]
        [InlineData("0-0", "1-0", -1)]
        [InlineData("1-0", "0-0", 1)]
        [InlineData("1789920825000-0", "1789920825000-0", 0)]
        [InlineData("1789920825000-0", "1789920825000-1", -1)]
        [InlineData("1789920825000-5", "1789920825000-2", 1)]
        [InlineData("1789920825001-0", "1789920825000-9", 1)]
        [InlineData("1789920825000-9", "1789920825001-0", -1)]
        public void CompareStreamIds_HandlesAllOrderingCasesAccurately(string id1, string id2, int expectedSign)
        {
            int result = TelemetryArchiveWorker.CompareStreamIds(id1, id2);
            if (expectedSign == 0)
            {
                Assert.Equal(0, result);
            }
            else if (expectedSign < 0)
            {
                Assert.True(result < 0, $"Expected {id1} < {id2}, got {result}");
            }
            else
            {
                Assert.True(result > 0, $"Expected {id1} > {id2}, got {result}");
            }
        }
    }
}

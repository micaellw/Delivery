using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BackendApi.Services.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace BackendApi.Services.BackgroundWorkers.Jobs
{
    public class TelemetryStreamTrimGapException : InvalidOperationException
    {
        public TelemetryStreamTrimGapException(string message) : base(message) { }
        public TelemetryStreamTrimGapException(string message, Exception inner) : base(message, inner) { }
    }

    public class TelemetryArchivePoint
    {
        [JsonPropertyName("eventId")]
        public string EventId { get; set; } = string.Empty;

        [JsonPropertyName("riderId")]
        public string RiderId { get; set; } = string.Empty;

        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; } = string.Empty;

        [JsonPropertyName("lat")]
        public double Lat { get; set; }

        [JsonPropertyName("lng")]
        public double Lng { get; set; }

        [JsonPropertyName("accuracy")]
        public double Accuracy { get; set; }
    }

    /// <summary>
    /// Represents an assembled batch of telemetry points bounded by size (MaxBatchEntries)
    /// or time interval (MaxWaitInterval), with in-batch and cross-batch deduplication applied.
    /// </summary>
    public class TelemetryBatch
    {
        public string FirstStreamId { get; }
        public string LastStreamId { get; }
        public IReadOnlyList<StreamEntry> RawEntries { get; }
        public IReadOnlyList<StreamEntry> DeduplicatedEntries { get; }
        public DateTimeOffset CreatedAt { get; }

        public TelemetryBatch(
            string firstStreamId,
            string lastStreamId,
            IReadOnlyList<StreamEntry> rawEntries,
            IReadOnlyList<StreamEntry> deduplicatedEntries,
            DateTimeOffset createdAt)
        {
            FirstStreamId = firstStreamId ?? throw new ArgumentNullException(nameof(firstStreamId));
            LastStreamId = lastStreamId ?? throw new ArgumentNullException(nameof(lastStreamId));
            RawEntries = rawEntries ?? throw new ArgumentNullException(nameof(rawEntries));
            DeduplicatedEntries = deduplicatedEntries ?? throw new ArgumentNullException(nameof(deduplicatedEntries));
            CreatedAt = createdAt;
        }

        /// <summary>
        /// The unique entries to be archived into the object payload.
        /// </summary>
        public IReadOnlyList<StreamEntry> Entries => DeduplicatedEntries;

        public string ToPendingBatchPayload() => TelemetryArchiveWorker.FormatPendingBatch(FirstStreamId, LastStreamId);

        public string GenerateObjectKey() => TelemetryArchiveWorker.BuildObjectKey(CreatedAt, FirstStreamId, LastStreamId);
    }

    /// <summary>
    /// Phase 2.2 Background worker responsible for streaming telemetry data from 
    /// the ephemeral Redis Stream (telemetry:stream:gps) into cold immutable MinIO S3 archive objects.
    /// 
    /// Architecture Contracts:
    /// - StreamKey: telemetry:stream:gps
    /// - CheckpointKey: telemetry:archiver:last_id (committed stream cursor checkpoint)
    /// - PendingBatchKey: telemetry:archiver:pending_batch (staging boundary recovery metadata)
    /// - SeenPrefix: telemetry:archiver:seen:{eventId} (transient duplicate storm guard, TTL: 2h)
    /// - Cursor semantics: Strictly exclusive (XREAD reads entries with ID strictly > last_id)
    /// - Initial checkpoint: "0-0"
    /// - MaxBatchEntries: 5,000
    /// - MaxWaitInterval: 60 seconds
    /// - Object Key: gps/year=YYYY/month=MM/day=DD/hour=HH/batch_{firstStreamId}_{lastStreamId}.ndjson.gz
    /// - Pipeline Commit Sequence: Collect -> Dedup -> SET pending_batch -> Upload MinIO -> SET last_id -> DEL pending_batch
    /// - Recovery Semantics:
    ///     * Case A (Crash before upload): XRANGE A B -> Trim check -> Dedup -> Upload -> SET last_id -> DEL pending_batch
    ///     * Case B (Crash after upload, before checkpoint): Detect object in MinIO -> SET last_id -> DEL pending_batch
    ///     * Case C (Crash after checkpoint, before DEL pending): Checkpoint >= B -> DEL pending_batch
    ///     * Trim Gap Safety: If start ID 'A' missing from stream -> CRITICAL log -> Safe Halt (Throw)
    /// </summary>
    public class TelemetryArchiveWorker : BackgroundService
    {
        public const string StreamKey = "telemetry:stream:gps";
        public const string CheckpointKey = "telemetry:archiver:last_id";
        public const string PendingBatchKey = "telemetry:archiver:pending_batch";
        public const string SeenPrefix = "telemetry:archiver:seen:";
        public static readonly TimeSpan SeenTtl = TimeSpan.FromHours(2);

        public const int DefaultMaxBatchEntries = 5000;
        public static readonly TimeSpan DefaultMaxWaitInterval = TimeSpan.FromSeconds(60);

        private readonly IConnectionMultiplexer? _redis;
        private readonly ITelemetryArchiveStorage? _archiveStorage;
        private readonly ILogger<TelemetryArchiveWorker> _logger;
        private readonly TimeSpan _emptyStreamDelay;
        private readonly int _maxBatchEntries;
        private readonly TimeSpan _maxWaitInterval;
        private readonly TimeProvider _timeProvider;

        private readonly List<StreamEntry> _bufferedEntries = new();
        private DateTimeOffset? _batchStartTime;

        public string CurrentCheckpoint { get; private set; } = "0-0";
        public int BufferedCount => _bufferedEntries.Count;
        public DateTimeOffset? BatchStartTime => _batchStartTime;
        public bool IsHalted { get; private set; }

        public TelemetryArchiveWorker(
            IConnectionMultiplexer? redis,
            ITelemetryArchiveStorage? archiveStorage,
            ILogger<TelemetryArchiveWorker> logger,
            TimeSpan? emptyStreamDelay = null,
            int maxBatchEntries = DefaultMaxBatchEntries,
            TimeSpan? maxWaitInterval = null,
            TimeProvider? timeProvider = null)
        {
            _redis = redis;
            _archiveStorage = archiveStorage;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _emptyStreamDelay = emptyStreamDelay ?? TimeSpan.FromSeconds(1);
            _maxBatchEntries = maxBatchEntries > 0 ? maxBatchEntries : DefaultMaxBatchEntries;
            _maxWaitInterval = maxWaitInterval ?? DefaultMaxWaitInterval;
            _timeProvider = timeProvider ?? TimeProvider.System;
        }

        public async Task InitializeCheckpointAsync(CancellationToken ct = default)
        {
            var redisDb = _redis?.GetDatabase();
            if (redisDb != null)
            {
                var stored = await redisDb.StringGetAsync(CheckpointKey, CommandFlags.None);
                if (stored.HasValue && !string.IsNullOrWhiteSpace(stored))
                {
                    CurrentCheckpoint = stored.ToString();
                }
                else
                {
                    CurrentCheckpoint = "0-0";
                }

                _logger.LogInformation("TelemetryArchiveWorker checkpoint initialized to {Checkpoint}", CurrentCheckpoint);

                // Startup crash recovery for pending batch (Sub-step 2.2-B.2.5)
                await RecoverPendingBatchAsync(ct);
            }
            else
            {
                CurrentCheckpoint = "0-0";
                _logger.LogInformation("TelemetryArchiveWorker checkpoint initialized to {Checkpoint} (Redis unavailable)", CurrentCheckpoint);
            }
        }

        /// <summary>
        /// Recovers any pending batch recorded in Redis due to an ungraceful shutdown or crash.
        /// Handles:
        /// - Case C: Checkpoint already reached or exceeded lastStreamId -> Clean stale pending key.
        /// - Case B: MinIO object already exists via deterministic key -> Advance checkpoint, clear pending key.
        /// - Case A: Object does not exist -> Read XRANGE, verify Trim Safety, Dedup, Upload, Advance checkpoint, clear pending key.
        /// - Trim Safety: If start ID missing from stream -> CRITICAL log -> Safe Halt.
        /// </summary>
        public async Task<bool> RecoverPendingBatchAsync(CancellationToken ct = default)
        {
            var redisDb = _redis?.GetDatabase();
            if (redisDb == null) return false;

            var pendingValue = await redisDb.StringGetAsync(PendingBatchKey, CommandFlags.None);
            if (!pendingValue.HasValue || string.IsNullOrWhiteSpace(pendingValue))
            {
                return false; // Clean startup, no pending recovery needed
            }

            var parsed = ParsePendingBatch(pendingValue.ToString());
            if (parsed == null)
            {
                _logger.LogWarning("Corrupted pending batch payload found in Redis: '{Payload}'. Deleting staging key.", pendingValue);
                await redisDb.KeyDeleteAsync(PendingBatchKey, CommandFlags.None);
                return false;
            }

            var (firstStreamId, lastStreamId) = parsed.Value;
            _logger.LogInformation(
                "TelemetryArchiveWorker detected pending batch in recovery: {FirstStreamId} -> {LastStreamId} (CurrentCheckpoint={Checkpoint})",
                firstStreamId, lastStreamId, CurrentCheckpoint);

            // Case C Check: Has checkpoint already advanced to or past lastStreamId?
            if (CompareStreamIds(CurrentCheckpoint, lastStreamId) >= 0)
            {
                _logger.LogInformation(
                    "Recovery [Case C]: Checkpoint ({Checkpoint}) already committed for pending batch ({LastStreamId}). Clearing staging key.",
                    CurrentCheckpoint, lastStreamId);
                await redisDb.KeyDeleteAsync(PendingBatchKey, CommandFlags.None);
                return true;
            }

            // Deterministic object key generation for existence check
            var batchTime = ExtractTimestampFromStreamId(firstStreamId, _timeProvider.GetUtcNow());
            var objectKey = BuildObjectKey(batchTime, firstStreamId, lastStreamId);

            // Case B Check: Did MinIO upload succeed before crash while checkpoint commit failed?
            bool objectExists = _archiveStorage != null && await _archiveStorage.ObjectExistsAsync(objectKey, ct);
            if (objectExists)
            {
                _logger.LogInformation(
                    "Recovery [Case B]: Deterministic object '{ObjectKey}' already exists in MinIO cold storage. Committing checkpoint to {LastStreamId} and clearing staging key.",
                    objectKey, lastStreamId);

                await redisDb.StringSetAsync(CheckpointKey, lastStreamId, null, When.Always, CommandFlags.None);
                CurrentCheckpoint = lastStreamId;
                await redisDb.KeyDeleteAsync(PendingBatchKey, CommandFlags.None);
                return true;
            }

            // Case A: MinIO upload was not completed before crash. Re-read stream range.
            _logger.LogInformation(
                "Recovery [Case A]: Object '{ObjectKey}' not found in MinIO. Re-reading stream range {FirstStreamId} -> {LastStreamId} from Redis.",
                objectKey, firstStreamId, lastStreamId);

            var entries = await redisDb.StreamRangeAsync(StreamKey, firstStreamId, lastStreamId, null, Order.Ascending, CommandFlags.None);

            // Trim Gap Safety Check: Is entry 'firstStreamId' intact in Redis Stream?
            if (entries == null || entries.Length == 0 || entries[0].Id.ToString() != firstStreamId)
            {
                IsHalted = true;
                var oldestAvailable = entries?.Length > 0 ? entries[0].Id.ToString() : "none";
                _logger.LogCritical(
                    "CRITICAL ARCHITECTURE VIOLATION: Stream trim-gap detected during recovery! Pending batch start {FirstStreamId} was pruned from Redis Stream {StreamKey}. Oldest available entry: {OldestAvailable}. Entering SAFE HALT.",
                    firstStreamId, StreamKey, oldestAvailable);

                throw new TelemetryStreamTrimGapException(
                    $"Pending batch start '{firstStreamId}' is missing from stream '{StreamKey}'. Data loss or trim gap detected. Halting archiver safely.");
            }

            // Process recovered batch through dedup and upload
            var deduplicated = await DeduplicateEntriesAsync(entries, redisDb);
            var recoveredBatch = new TelemetryBatch(firstStreamId, lastStreamId, entries, deduplicated, batchTime);

            await UploadBatchAsync(recoveredBatch, ct);

            // Commit checkpoint and clear pending staging
            await redisDb.StringSetAsync(CheckpointKey, lastStreamId, null, When.Always, CommandFlags.None);
            CurrentCheckpoint = lastStreamId;
            await redisDb.KeyDeleteAsync(PendingBatchKey, CommandFlags.None);

            _logger.LogInformation(
                "Recovery [Case A]: Successfully uploaded batch and committed checkpoint to {LastStreamId}.",
                lastStreamId);

            return true;
        }

        /// <summary>
        /// Executes the strict 4-step pipeline:
        /// 1. SET pending_batch before upload
        /// 2. Upload MinIO
        /// 3. SET last_id = lastStreamId (strictly after upload succeeds)
        /// 4. DEL pending_batch (clears staging)
        /// </summary>
        public async Task ProcessBatchPipelineAsync(TelemetryBatch batch, CancellationToken ct = default)
        {
            if (batch == null) throw new ArgumentNullException(nameof(batch));
            var redisDb = _redis?.GetDatabase() ?? throw new InvalidOperationException("Redis database connection is unavailable.");

            // Step 1: Stage pending batch metadata before attempting upload
            var pendingPayload = batch.ToPendingBatchPayload();
            await redisDb.StringSetAsync(PendingBatchKey, pendingPayload, null, When.Always, CommandFlags.None);
            _logger.LogDebug("Staged pending batch: {Payload}", pendingPayload);

            // Step 2: Upload to MinIO cold storage (If upload fails, exception propagates and checkpoint is NOT modified)
            var uploadedKey = await UploadBatchAsync(batch, ct);

            // Step 3: Advance checkpoint strictly after upload succeeds
            await redisDb.StringSetAsync(CheckpointKey, batch.LastStreamId, null, When.Always, CommandFlags.None);
            CurrentCheckpoint = batch.LastStreamId;
            _logger.LogInformation("Committed checkpoint: {Checkpoint} for object: {ObjectKey}", CurrentCheckpoint, uploadedKey);

            // Step 4: Clear pending batch staging metadata
            await redisDb.KeyDeleteAsync(PendingBatchKey, CommandFlags.None);
            _logger.LogDebug("Cleared pending batch staging for: {Payload}", pendingPayload);
        }

        public static Guid? ExtractEventId(StreamEntry entry)
        {
            foreach (var nv in entry.Values)
            {
                if (nv.Name == "eventId" && Guid.TryParse(nv.Value, out var guid))
                {
                    return guid;
                }
            }
            return null;
        }

        public static TelemetryArchivePoint ParsePoint(StreamEntry entry)
        {
            var point = new TelemetryArchivePoint();
            foreach (var nv in entry.Values)
            {
                switch (nv.Name)
                {
                    case "eventId":
                        point.EventId = nv.Value.ToString();
                        break;
                    case "riderId":
                        point.RiderId = nv.Value.ToString();
                        break;
                    case "timestamp":
                        point.Timestamp = nv.Value.ToString();
                        break;
                    case "lat":
                        if (double.TryParse(nv.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat))
                            point.Lat = lat;
                        break;
                    case "lng":
                        if (double.TryParse(nv.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var lng))
                            point.Lng = lng;
                        break;
                    case "accuracy":
                        if (double.TryParse(nv.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var acc))
                            point.Accuracy = acc;
                        break;
                }
            }
            return point;
        }

        public static byte[] SerializeToNdjson(IEnumerable<StreamEntry> entries)
        {
            using var ms = new MemoryStream();
            foreach (var entry in entries)
            {
                var point = ParsePoint(entry);
                var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(point);
                ms.Write(jsonBytes, 0, jsonBytes.Length);
                ms.WriteByte((byte)'\n');
            }
            return ms.ToArray();
        }

        public static byte[] CompressGzip(byte[] rawBytes)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                gzip.Write(rawBytes, 0, rawBytes.Length);
                gzip.Flush();
            }
            return output.ToArray();
        }

        /// <summary>
        /// Deduplicates raw stream entries using:
        /// 1. Guaranteed in-batch deduplication via HashSet&lt;Guid&gt; on eventId.
        /// 2. Best-effort active cross-batch deduplication via transient Redis seen key (TTL: 2 hours).
        /// Note: Filtered entries still belong to the batch boundary; only unique entries are returned for archival payload.
        /// </summary>
        public async Task<IReadOnlyList<StreamEntry>> DeduplicateEntriesAsync(
            IReadOnlyList<StreamEntry> entries,
            IDatabase redisDb)
        {
            if (entries == null || entries.Count == 0)
            {
                return Array.Empty<StreamEntry>();
            }

            var inBatchSeen = new HashSet<Guid>();
            var candidateEntries = new List<(StreamEntry entry, Guid eventId)>(entries.Count);

            // 1. In-batch Deduplication (Guaranteed within archive object)
            foreach (var entry in entries)
            {
                var eventId = ExtractEventId(entry);
                if (eventId.HasValue)
                {
                    if (inBatchSeen.Add(eventId.Value))
                    {
                        candidateEntries.Add((entry, eventId.Value));
                    }
                    else
                    {
                        _logger.LogDebug("In-batch duplicate filtered: eventId={EventId}, streamId={StreamId}", eventId.Value, entry.Id);
                    }
                }
                else
                {
                    candidateEntries.Add((entry, Guid.Empty));
                }
            }

            // 2. Cross-batch Deduplication via transient Redis seen key (TTL: 2h)
            var deduplicated = new List<StreamEntry>(candidateEntries.Count);
            var seenTasks = new List<Task>();

            foreach (var (entry, eventId) in candidateEntries)
            {
                if (eventId == Guid.Empty)
                {
                    deduplicated.Add(entry);
                    continue;
                }

                var seenKey = SeenPrefix + eventId.ToString("D");
                bool isSeen = await redisDb.KeyExistsAsync(seenKey, CommandFlags.None);

                if (!isSeen)
                {
                    deduplicated.Add(entry);
                    seenTasks.Add(redisDb.StringSetAsync(seenKey, "1", SeenTtl, When.Always, CommandFlags.None));
                }
                else
                {
                    _logger.LogDebug("Cross-batch duplicate filtered: eventId={EventId}, streamId={StreamId}", eventId, entry.Id);
                }
            }

            if (seenTasks.Count > 0)
            {
                await Task.WhenAll(seenTasks);
            }

            return deduplicated;
        }

        /// <summary>
        /// Attempts to collect entries from Redis Stream and returns a TelemetryBatch if boundary conditions are met:
        /// 1. Immediately if buffered count reaches MaxBatchEntries (5,000).
        /// 2. If buffered count > 0 and MaxWaitInterval (60s) has elapsed.
        /// Returns null if more entries are needed or stream is empty.
        /// </summary>
        public async Task<TelemetryBatch?> CollectBatchAsync(CancellationToken ct = default)
        {
            var redisDb = _redis?.GetDatabase();
            if (redisDb == null)
            {
                _logger.LogWarning("TelemetryArchiveWorker: Redis database connection is unavailable.");
                return null;
            }

            var now = _timeProvider.GetUtcNow();

            // Check if existing partial batch has reached timeout before reading
            if (_bufferedEntries.Count > 0 && _batchStartTime.HasValue && (now - _batchStartTime.Value) >= _maxWaitInterval)
            {
                return await FlushCurrentBufferAsync(now, redisDb);
            }

            int neededCount = _maxBatchEntries - _bufferedEntries.Count;
            if (neededCount <= 0)
            {
                return await FlushCurrentBufferAsync(now, redisDb);
            }

            // Exclusive cursor: read position is strictly greater than the last buffered entry or CurrentCheckpoint
            var readPosition = _bufferedEntries.Count > 0
                ? _bufferedEntries[^1].Id
                : new RedisValue(CurrentCheckpoint);

            var entries = await redisDb.StreamReadAsync(StreamKey, readPosition, count: neededCount, CommandFlags.None);

            if (entries != null && entries.Length > 0)
            {
                if (_bufferedEntries.Count == 0)
                {
                    _batchStartTime = now;
                }

                _bufferedEntries.AddRange(entries);
                _logger.LogDebug("TelemetryArchiveWorker buffered {NewCount} entries (total: {Total}/{Max})", entries.Length, _bufferedEntries.Count, _maxBatchEntries);

                // Rule 1: Flush immediately if batch reaches MaxBatchEntries
                if (_bufferedEntries.Count >= _maxBatchEntries)
                {
                    return await FlushCurrentBufferAsync(now, redisDb);
                }
            }

            // Rule 2: Check timeout again after reading
            if (_bufferedEntries.Count > 0 && _batchStartTime.HasValue)
            {
                now = _timeProvider.GetUtcNow();
                if ((now - _batchStartTime.Value) >= _maxWaitInterval)
                {
                    return await FlushCurrentBufferAsync(now, redisDb);
                }
            }

            return null;
        }

        private async Task<TelemetryBatch> FlushCurrentBufferAsync(DateTimeOffset timestamp, IDatabase redisDb)
        {
            if (_bufferedEntries.Count == 0)
            {
                throw new InvalidOperationException("Cannot flush an empty buffer");
            }

            var firstId = _bufferedEntries[0].Id.ToString();
            var lastId = _bufferedEntries[^1].Id.ToString();
            var rawEntries = _bufferedEntries.ToArray();

            _bufferedEntries.Clear();
            _batchStartTime = null;

            // Apply Deduplication Hierarchy (In-batch HashSet + Cross-batch seen key)
            var deduplicated = await DeduplicateEntriesAsync(rawEntries, redisDb);

            _logger.LogInformation(
                "TelemetryArchiveWorker assembled batch: first={FirstId}, last={LastId}, rawCount={RawCount}, uniqueCount={UniqueCount}",
                firstId, lastId, rawEntries.Length, deduplicated.Count);

            var batchTime = ExtractTimestampFromStreamId(firstId, timestamp);
            return new TelemetryBatch(firstId, lastId, rawEntries, deduplicated, batchTime);
        }

        /// <summary>
        /// Serializes the batch's DeduplicatedEntries into NDJSON format, compresses with GZip,
        /// and uploads the archive object to MinIO cold storage via ITelemetryArchiveStorage.
        /// Returns the deterministic object key.
        /// </summary>
        public async Task<string> UploadBatchAsync(TelemetryBatch batch, CancellationToken ct = default)
        {
            if (batch == null) throw new ArgumentNullException(nameof(batch));
            if (_archiveStorage == null)
            {
                throw new InvalidOperationException("TelemetryArchiveWorker: ITelemetryArchiveStorage is not configured.");
            }

            var objectKey = batch.GenerateObjectKey();
            var ndjsonBytes = SerializeToNdjson(batch.DeduplicatedEntries);
            var gzipBytes = CompressGzip(ndjsonBytes);

            using var uploadStream = new MemoryStream(gzipBytes);

            _logger.LogInformation(
                "Uploading telemetry archive object: key={ObjectKey}, rawCount={RawCount}, dedupCount={DedupCount}, rawBytes={RawBytes}, gzBytes={GzBytes}",
                objectKey, batch.RawEntries.Count, batch.DeduplicatedEntries.Count, ndjsonBytes.Length, gzipBytes.Length);

            var resultKey = await _archiveStorage.PutArchiveObjectAsync(
                uploadStream,
                objectKey,
                contentType: "application/gzip",
                ct: ct);

            return resultKey;
        }

        /// <summary>
        /// Reads next batch from Redis Stream and processes through the full archival pipeline.
        /// </summary>
        public async Task<int> ProcessNextBatchAsync(CancellationToken stoppingToken = default)
        {
            var batch = await CollectBatchAsync(stoppingToken);
            if (batch != null)
            {
                await ProcessBatchPipelineAsync(batch, stoppingToken);
                return batch.RawEntries.Count;
            }
            return _bufferedEntries.Count;
        }

        public static string BuildObjectKey(DateTimeOffset timestamp, string firstStreamId, string lastStreamId)
        {
            var utc = timestamp.UtcDateTime;
            return $"gps/year={utc.Year:D4}/month={utc.Month:D2}/day={utc.Day:D2}/hour={utc.Hour:D2}/batch_{firstStreamId}_{lastStreamId}.ndjson.gz";
        }

        public static string FormatPendingBatch(string firstStreamId, string lastStreamId)
        {
            return $"{firstStreamId}|{lastStreamId}";
        }

        public static (string firstStreamId, string lastStreamId)? ParsePendingBatch(string? payload)
        {
            if (string.IsNullOrWhiteSpace(payload)) return null;
            var parts = payload.Split('|');
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            {
                return null;
            }
            return (parts[0], parts[1]);
        }

        public static DateTimeOffset ExtractTimestampFromStreamId(string streamId, DateTimeOffset fallback)
        {
            if (string.IsNullOrWhiteSpace(streamId)) return fallback;
            var dashIndex = streamId.IndexOf('-');
            if (dashIndex > 0 && long.TryParse(streamId.AsSpan(0, dashIndex), out var ms) && ms > 0)
            {
                try
                {
                    return DateTimeOffset.FromUnixTimeMilliseconds(ms);
                }
                catch
                {
                    return fallback;
                }
            }
            return fallback;
        }

        public static int CompareStreamIds(string id1, string id2)
        {
            if (id1 == id2) return 0;
            if (string.IsNullOrEmpty(id1)) return string.IsNullOrEmpty(id2) ? 0 : -1;
            if (string.IsNullOrEmpty(id2)) return 1;

            var (ms1, seq1) = ParseStreamIdParts(id1);
            var (ms2, seq2) = ParseStreamIdParts(id2);

            int msCompare = ms1.CompareTo(ms2);
            if (msCompare != 0) return msCompare;
            return seq1.CompareTo(seq2);
        }

        private static (long ms, long seq) ParseStreamIdParts(string id)
        {
            var dashIndex = id.IndexOf('-');
            if (dashIndex > 0 &&
                long.TryParse(id.AsSpan(0, dashIndex), out var ms) &&
                long.TryParse(id.AsSpan(dashIndex + 1), out var seq))
            {
                return (ms, seq);
            }
            return (0, 0);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("TelemetryArchiveWorker starting...");

            try
            {
                await InitializeCheckpointAsync(stoppingToken);

                while (!stoppingToken.IsCancellationRequested && !IsHalted)
                {
                    var batch = await CollectBatchAsync(stoppingToken);

                    if (batch == null)
                    {
                        await Task.Delay(_emptyStreamDelay, stoppingToken);
                    }
                    else
                    {
                        try
                        {
                            await ProcessBatchPipelineAsync(batch, stoppingToken);
                        }
                        catch (TelemetryStreamTrimGapException)
                        {
                            _logger.LogCritical("TelemetryArchiveWorker safely halted due to trim gap.");
                            break;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to process telemetry archive batch {First} -> {Last}. Retrying on next iteration.", batch.FirstStreamId, batch.LastStreamId);
                            await Task.Delay(_emptyStreamDelay, stoppingToken);
                        }
                    }
                }
            }
            catch (TelemetryStreamTrimGapException ex)
            {
                _logger.LogCritical(ex, "TelemetryArchiveWorker entered SAFE HALT on startup.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("TelemetryArchiveWorker stopping gracefully.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TelemetryArchiveWorker encountered an unexpected error.");
            }
        }
    }
}
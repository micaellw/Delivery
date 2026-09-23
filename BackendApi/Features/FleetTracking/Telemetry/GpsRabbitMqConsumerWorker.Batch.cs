using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using BackendApi.Data;
using BackendApi.Models;
using BackendApi.Models.Entities;
using BackendApi.Models.SystemModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Globalization;
using StackExchange.Redis;

namespace BackendApi.Features.FleetTracking.Telemetry
{
    /// <summary>
    /// Background service that consumes GPS telemetry messages from 'gps_telemetry_queue'.
    /// Implements high-performance Mega-Batching with a Prefetch Count of 5000,
    /// a local bounded channel for thread separation, and manual Batch ACKs
    /// after successful PostgreSQL insert to guarantee Zero Data Loss.
    /// </summary>
    public partial class GpsRabbitMqConsumerWorker
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                InitializeRabbitMq();
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Failed to initialize RabbitMQ for GPS Telemetry Consumer. Exiting application.");
                _appLifetime.StopApplication();
                return;
            }

            _logger.LogInformation("GpsRabbitMqConsumerWorker background processor loop started.");

            try
            {
                // Main reading loop from the local C# Channel
                while (await _localChannel.Reader.WaitToReadAsync(stoppingToken))
                {
                    try
                    {
                        await DrainAndSaveBatchAsync(stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error occurred while draining and saving GPS batches.");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
            finally
            {
                _logger.LogInformation("GpsRabbitMqConsumerWorker stopping. Draining remaining local buffer...");

                // Complete the local channel writer
                _localChannel.Writer.TryComplete();

                try
                {
                    // Do a final drain of any remaining items in the local channel
                    await DrainAndSaveBatchAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error occurred during final flush of GpsRabbitMqConsumerWorker");
                }

                _channel?.Dispose();
                _connection?.Dispose();
            }
        }

        /// <summary>
        /// Drains messages up to SubBatchLimit, saves them to PostGIS, and performs a manual ACK.
        /// </summary>
        private static Guid GenerateGuidFromPoint(TrackPoint point)
        {
            string key = $"{point.RiderId}_{point.Timestamp.Ticks}";
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            return new Guid(hash.AsSpan(0, 16));
        }

        private async Task DrainAndSaveBatchAsync(CancellationToken ct)
        {
            var points = new List<TrackPoint>(SubBatchLimit);
            var deliveryTags = new List<ulong>(SubBatchLimit);

            // Read up to SubBatchLimit elements that are immediately available
            while (points.Count < SubBatchLimit && _localChannel.Reader.TryRead(out var item))
            {
                points.Add(item.Point);
                deliveryTags.Add(item.DeliveryTag);
            }

            if (points.Count == 0) return;

            var batchCorrelationId = Guid.NewGuid().ToString();

            // Trace Correlation Scope
            using (_logger.BeginScope(new Dictionary<string, object>
            {
                ["CorrelationId"] = batchCorrelationId,
                ["BatchSize"] = points.Count
            }))
            {
                _logger.LogInformation("Drained {Count} GPS points from local channel. Committing to database...", points.Count);

                try
                {
                    var redisDb = _redis?.GetDatabase();
                    var duplicatePoints = new List<TrackPoint>();
                    var duplicateEventIds = new List<Guid>();

                    // Save to database within scoped context
                    using (var scope = _serviceProvider.CreateScope())
                    {
                        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                        var historyService = scope.ServiceProvider.GetRequiredService<BackendApi.Services.Telemetry.GpsHistoryService>();

                        // Wrap processed events validation and actual GPS points saving in a single atomic transaction (Zero-Data-Loss Fix)
                        using (var transaction = await dbContext.Database.BeginTransactionAsync(ct))
                        {
                            try
                            {
                                var newPoints = new List<TrackPoint>();
                                var newEventIds = new List<Guid>();
                                var processedEvents = new List<ProcessedEvent>();

                                // Bulk Check ProcessedEvents for duplicates
                                var eventIdsToCheck = points.Select(p => GenerateGuidFromPoint(p)).ToList();
                                var existingEventIds = await dbContext.ProcessedEvents
                                    .Where(pe => pe.HandlerName == "GpsConsumer" && eventIdsToCheck.Contains(pe.EventId))
                                    .Select(pe => pe.EventId)
                                    .ToListAsync(ct);

                                var existingEventSet = new HashSet<Guid>(existingEventIds);
                                var uniqueEventIdsInBatch = new HashSet<Guid>();
                                for (int i = 0; i < points.Count; i++)
                                {
                                    var p = points[i];
                                    var eventId = eventIdsToCheck[i];
                                    
                                    if (!existingEventSet.Contains(eventId))
                                    {
                                        if (uniqueEventIdsInBatch.Add(eventId))
                                        {
                                            newPoints.Add(p);
                                            newEventIds.Add(eventId);
                                            processedEvents.Add(new ProcessedEvent
                                            {
                                                EventId = eventId,
                                                HandlerName = "GpsConsumer",
                                                ProcessedAt = DateTime.UtcNow
                                            });
                                        }
                                    }
                                    else
                                    {
                                        duplicatePoints.Add(p);
                                        duplicateEventIds.Add(eventId);
                                    }
                                }

                                if (newPoints.Count > 0)
                                {
                                    // Save processed events for idempotency (Rule 3)
                                    dbContext.ProcessedEvents.AddRange(processedEvents);
                                    await dbContext.SaveChangesAsync(ct);

                                    // Save new unique GPS points to PostgreSQL
                                    await historyService.SavePointsAsync(newPoints, ct);

                                    // Dual-write to Redis Stream before committing DB transaction
                                    if (redisDb != null)
                                    {
                                        await WriteToRedisStreamAsync(redisDb, newPoints, newEventIds);
                                    }
                                }

                                await transaction.CommitAsync(ct);
                                _logger.LogInformation("Successfully committed batch of {Count} GPS points (New unique: {UniqueCount}) to PostgreSQL.", points.Count, newPoints.Count);
                            }
                            catch (Exception)
                            {
                                await transaction.RollbackAsync(ct);
                                throw;
                            }
                        }
                    }

                    // Process duplicate points: Case 5 Redelivery repair guard
                    if (redisDb != null && duplicatePoints.Count > 0)
                    {
                        var repairPoints = new List<TrackPoint>();
                        var repairEventIds = new List<Guid>();

                        var checkTasks = duplicateEventIds.Select(id => redisDb.KeyExistsAsync(StreamSeenPrefix + id.ToString("D"))).ToArray();
                        await Task.WhenAll(checkTasks);

                        for (int i = 0; i < checkTasks.Length; i++)
                        {
                            if (!checkTasks[i].Result)
                            {
                                repairPoints.Add(duplicatePoints[i]);
                                repairEventIds.Add(duplicateEventIds[i]);
                            }
                        }

                        if (repairPoints.Count > 0)
                        {
                            _logger.LogWarning("Detected {Count} redelivered GPS points missing from Redis Stream. Repairing stream pipeline...", repairPoints.Count);
                            await WriteToRedisStreamAsync(redisDb, repairPoints, repairEventIds);
                        }
                        else
                        {
                            _logger.LogDebug("All {Count} redelivered GPS points already exist in Redis Stream guard. Skipping duplicate XADD.", duplicatePoints.Count);
                        }
                    }

                    // Successful Database Save & Dual-Write -> Bulk ACK to RabbitMQ
                    ulong maxDeliveryTag = 0;
                    foreach (var tag in deliveryTags)
                    {
                        if (tag > maxDeliveryTag) maxDeliveryTag = tag;
                    }

                    if (maxDeliveryTag > 0 && _channel != null)
                    {
                        lock (_channel) // Make sure basic ack is thread-safe on the channel
                        {
                            _channel.BasicAck(maxDeliveryTag, multiple: true);
                        }
                        _logger.LogInformation("Batch of {Count} GPS points successfully ACKed to RabbitMQ up to tag {Tag}.", points.Count, maxDeliveryTag);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to commit batch of {Count} GPS points to the database / Redis stream. Messages will be NACKed to DLQ.", points.Count);
                    
                    ulong maxDeliveryTag = 0;
                    foreach (var tag in deliveryTags)
                    {
                        if (tag > maxDeliveryTag) maxDeliveryTag = tag;
                    }

                    if (maxDeliveryTag > 0 && _channel != null)
                    {
                        lock (_channel) 
                        {
                            // NACK the batch. requeue: false sends them to the Dead Letter Queue.
                            _channel.BasicNack(maxDeliveryTag, multiple: true, requeue: false);
                        }
                        _logger.LogInformation("Batch of {Count} GPS points successfully NACKed up to tag {Tag}.", points.Count, maxDeliveryTag);
                    }
                }
            }
        }

        /// <summary>
        /// Writes GPS points to Redis Stream with approximate trimming (MAXLEN ~ 100,000)
        /// and records short-lived duplicate storm guard keys (TTL: 1 hour).
        /// </summary>
        private static async Task WriteToRedisStreamAsync(IDatabase redisDb, List<TrackPoint> points, List<Guid> eventIds)
        {
            var tasks = new List<Task>(points.Count * 2);

            for (int i = 0; i < points.Count; i++)
            {
                var p = points[i];
                var eventId = eventIds[i];
                var eventIdStr = eventId.ToString("D");

                var entries = new NameValueEntry[]
                {
                    new("eventId", eventIdStr),
                    new("riderId", p.RiderId),
                    new("timestamp", p.Timestamp.ToUniversalTime().ToString("o")),
                    new("lat", p.Lat.ToString("G17", CultureInfo.InvariantCulture)),
                    new("lng", p.Lng.ToString("G17", CultureInfo.InvariantCulture)),
                    new("accuracy", "0.0")
                };

                // Approximate trimming MAXLEN ~ 100000 (Memory Bounding Policy)
                tasks.Add(redisDb.StreamAddAsync(StreamKey, entries, maxLength: StreamMaxLen, useApproximateMaxLength: true));
                // Short-lived duplicate storm guard (TTL: 1 hour)
                tasks.Add(redisDb.StringSetAsync(StreamSeenPrefix + eventIdStr, "1", StreamSeenTtl));
            }

            await Task.WhenAll(tasks);
        }
    }
}
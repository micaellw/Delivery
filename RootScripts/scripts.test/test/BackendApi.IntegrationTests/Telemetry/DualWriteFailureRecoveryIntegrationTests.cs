using System.Reflection;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BackendApi.Data;
using BackendApi.Features.FleetTracking.Telemetry;
using BackendApi.Models;
using BackendApi.Models.Entities;
using BackendApi.Services.Auth;
using BackendApi.Services.BackgroundWorkers.Jobs;
using BackendApi.Services.Storage;
using BackendApi.Services.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using StackExchange.Redis;
using Xunit;

namespace BackendApi.IntegrationTests.Telemetry
{
    [Collection("SequentialLiveIntegrationTests")]
    public class DualWriteFailureRecoveryIntegrationTests
    {
        private const string PgConnectionString = "Host=localhost;Port=5442;Database=delivery_db;Username=postgres;Password=Admin@Ts2x04_;Include Error Detail=true;";
        private const string RedisConnectionString = "127.0.0.1:6389,password=Password123!,abortConnect=false";
        private const string RabbitHost = "127.0.0.1";
        private const int RabbitPort = 5682;
        private const string RabbitUser = "guest";
        private const string RabbitPass = "guest";

        private const string MinioEndpoint = "127.0.0.1:9002";
        private const string MinioAccessKey = "minioadmin";
        private const string MinioSecretKey = "miniopassword123";
        private const string ArchiveBucketName = "delivery-telemetry-archive";

        private const string PrimaryQueueName = "gps_telemetry_queue";
        private const string DlqName = "gps_telemetry_queue_dlq";

        private class TestCurrentUserService : ICurrentUserService
        {
            public Guid? UserId => Guid.NewGuid();
            public string? UserName => "IntegrationTestUser";
            public string? IpAddress => "127.0.0.1";
        }

        private class TestHostLifetime : IHostApplicationLifetime
        {
            private readonly CancellationTokenSource _cts = new();
            public CancellationToken ApplicationStarted => _cts.Token;
            public CancellationToken ApplicationStopping => _cts.Token;
            public CancellationToken ApplicationStopped => _cts.Token;
            public void StopApplication() => _cts.Cancel();
        }

        private class FailCommitTransactionInterceptor : DbTransactionInterceptor
        {
            public bool ShouldFailCommit { get; set; } = true;

            public override ValueTask<InterceptionResult> TransactionCommittingAsync(
                DbTransaction transaction,
                TransactionEventData eventData,
                InterceptionResult result,
                CancellationToken cancellationToken = default)
            {
                if (ShouldFailCommit)
                {
                    throw new Npgsql.NpgsqlException("Simulated PostgreSQL commit connection failure during two-phase write window");
                }
                return base.TransactionCommittingAsync(transaction, eventData, result, cancellationToken);
            }
        }

        private class DecoratingConnectionProxy : DispatchProxy
        {
            public IConnection Target { get; set; } = null!;
            public Func<IModel, IModel>? ModelDecorator { get; set; }

            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                if (targetMethod == null) return null;
                if (targetMethod.Name == nameof(IConnection.CreateModel) && (args == null || args.Length == 0))
                {
                    var realModel = (IModel)targetMethod.Invoke(Target, args)!;
                    return ModelDecorator != null ? ModelDecorator(realModel) : realModel;
                }
                return targetMethod.Invoke(Target, args);
            }
        }

        private class CrashBeforeAckChannelProxy : DispatchProxy
        {
            public IModel Target { get; set; } = null!;
            public TaskCompletionSource<bool> CrashTriggeredTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                if (targetMethod == null) return null;

                if (targetMethod.Name == nameof(IModel.BasicAck))
                {
                    try { Target.Abort(); } catch { }
                    CrashTriggeredTcs.TrySetResult(true);
                    throw new InvalidOperationException("Simulated worker process crash immediately before BasicAck");
                }

                if (targetMethod.Name == nameof(IModel.BasicNack) && CrashTriggeredTcs.Task.IsCompleted)
                {
                    return null;
                }

                if (targetMethod.Name == nameof(IDisposable.Dispose))
                {
                    try { Target.Dispose(); } catch { }
                    return null;
                }

                return targetMethod.Invoke(Target, args);
            }
        }

        private static IConnection CreateConnectionProxy(IConnection realConn, Func<IModel, IModel> modelDecorator)
        {
            var proxy = DispatchProxy.Create<IConnection, DecoratingConnectionProxy>();
            var impl = (DecoratingConnectionProxy)(object)proxy;
            impl.Target = realConn;
            impl.ModelDecorator = modelDecorator;
            return proxy;
        }

        private static IModel CreateCrashChannelProxy(IModel realModel, out CrashBeforeAckChannelProxy proxyImpl)
        {
            var proxy = DispatchProxy.Create<IModel, CrashBeforeAckChannelProxy>();
            proxyImpl = (CrashBeforeAckChannelProxy)(object)proxy;
            proxyImpl.Target = realModel;
            return proxy;
        }

        private class CrashBeforeAckWorker : GpsRabbitMqConsumerWorker
        {
            private readonly Func<IConnection, IConnection> _connectionDecorator;

            public CrashBeforeAckWorker(
                IServiceProvider serviceProvider,
                IConfiguration configuration,
                IHostApplicationLifetime appLifetime,
                ILogger<GpsRabbitMqConsumerWorker> logger,
                IConnectionMultiplexer? redis,
                Func<IConnection, IConnection> connectionDecorator)
                : base(serviceProvider, configuration, appLifetime, logger, redis)
            {
                _connectionDecorator = connectionDecorator;
            }

            protected override IConnection CreateConnection(ConnectionFactory factory)
            {
                var realConn = base.CreateConnection(factory);
                return _connectionDecorator(realConn);
            }
        }

        private static Guid ComputeDeterministicGuid(string riderId, long ticks)
        {
            string key = $"{riderId}_{ticks}";
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            return new Guid(hash.AsSpan(0, 16));
        }

        [Fact]
        public async Task SubStep_2_1_D_A_RedisDownBeforeDbCommit_RollbackAndDlqRecovery()
        {
            // =========================================================================
            // PHASE 1: Baseline Setup & Pre-conditions
            // =========================================================================
            var realRedis = await ConnectionMultiplexer.ConnectAsync(RedisConnectionString);
            var realRedisDb = realRedis.GetDatabase();

            var rabbitFactory = new ConnectionFactory
            {
                HostName = RabbitHost,
                Port = RabbitPort,
                UserName = RabbitUser,
                Password = RabbitPass,
                DispatchConsumersAsync = true
            };
            using var testRabbitConn = rabbitFactory.CreateConnection();
            using var testChannel = testRabbitConn.CreateModel();

            testChannel.QueuePurge(PrimaryQueueName);
            testChannel.QueuePurge(DlqName);

            var riderId = "rider-da-" + Guid.NewGuid().ToString("N")[..8];
            var timestamp = DateTime.UtcNow;
            var eventId = ComputeDeterministicGuid(riderId, timestamp.Ticks);

            var services = new ServiceCollection();
            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseNpgsql(PgConnectionString, o => o.UseNetTopologySuite()));
            services.AddScoped<ICurrentUserService, TestCurrentUserService>();
            services.AddScoped<GpsHistoryService>();
            services.AddLogging();
            var serviceProvider = services.BuildServiceProvider();

            using (var initScope = serviceProvider.CreateScope())
            {
                var db = initScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var existingPe = await db.ProcessedEvents.FindAsync(eventId, "GpsConsumer");
                if (existingPe != null) db.ProcessedEvents.Remove(existingPe);

                var existingHist = await db.RiderLocationHistories.Where(h => h.RiderId == riderId).ToListAsync();
                if (existingHist.Count > 0) db.RiderLocationHistories.RemoveRange(existingHist);

                await db.SaveChangesAsync();
            }

            await realRedisDb.KeyDeleteAsync("telemetry:stream:seen:" + eventId.ToString("D"));

            using (var verifyScope = serviceProvider.CreateScope())
            {
                var db = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                Assert.Null(await db.ProcessedEvents.FindAsync(eventId, "GpsConsumer"));
                Assert.Empty(await db.RiderLocationHistories.Where(h => h.RiderId == riderId).ToListAsync());
            }

            // =========================================================================
            // PHASE 2: Failure Injection - Redis Stream Write Fails
            // =========================================================================
            bool simulateRedisFailure = true;

            var mockRedisDb = new Mock<IDatabase>();

            mockRedisDb.Setup(d => d.StreamAddAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<NameValueEntry[]>(),
                It.IsAny<RedisValue?>(),
                It.IsAny<long?>(),
                It.IsAny<bool>(),
                It.IsAny<long?>(),
                It.IsAny<StreamTrimMode>(),
                It.IsAny<CommandFlags>()
            )).Returns(new Func<RedisKey, NameValueEntry[], RedisValue?, long?, bool, long?, StreamTrimMode, CommandFlags, Task<RedisValue>>((key, entries, id, maxLen, approx, limit, trim, flags) =>
            {
                if (simulateRedisFailure)
                {
                    throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Simulated Redis outage during dual-write");
                }
                return realRedisDb.StreamAddAsync(key, entries, id, maxLen, approx, limit, trim, flags);
            }));

            mockRedisDb.Setup(d => d.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<Expiration>(),
                It.IsAny<ValueCondition>(),
                It.IsAny<CommandFlags>()
            )).Returns(new Func<RedisKey, RedisValue, Expiration, ValueCondition, CommandFlags, Task<bool>>((key, value, expiry, when, flags) =>
            {
                if (simulateRedisFailure)
                {
                    throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Simulated Redis outage during dual-write");
                }
                return realRedisDb.StringSetAsync(key, value, expiry, when, flags);
            }));

            mockRedisDb.Setup(d => d.KeyExistsAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<CommandFlags>()
            )).Returns(new Func<RedisKey, CommandFlags, Task<bool>>((key, flags) =>
            {
                return realRedisDb.KeyExistsAsync(key, flags);
            }));

            var mockRedis = new Mock<IConnectionMultiplexer>();
            mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object?>())).Returns(mockRedisDb.Object);

            var workerConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["MessageBroker:Host"] = RabbitHost,
                    ["MessageBroker:Port"] = RabbitPort.ToString(),
                    ["MessageBroker:Username"] = RabbitUser,
                    ["MessageBroker:Password"] = RabbitPass
                })
                .Build();

            var lifetime = new TestHostLifetime();
            var logger = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug)).CreateLogger<GpsRabbitMqConsumerWorker>();

            var worker = new GpsRabbitMqConsumerWorker(
                serviceProvider,
                workerConfig,
                lifetime,
                logger,
                mockRedis.Object
            );

            using var workerCts = new CancellationTokenSource();
            var workerTask = worker.StartAsync(workerCts.Token);

            await Task.Delay(1000);

            // =========================================================================
            // PHASE 3: Publish GPS Event to Primary Queue
            // =========================================================================
            var point = new TrackPoint(riderId, 13.7563, 100.5018, timestamp);
            var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(point));

            var props = testChannel.CreateBasicProperties();
            props.Persistent = true;
            testChannel.BasicPublish(
                exchange: "",
                routingKey: PrimaryQueueName,
                basicProperties: props,
                body: payload
            );

            // =========================================================================
            // PHASE 4: Verify Failure Path: PG Rollback -> NACK -> DLQ
            // =========================================================================
            int dlqCount = 0;
            for (int i = 0; i < 60; i++)
            {
                await Task.Delay(100);
                var dlqOk = testChannel.QueueDeclarePassive(DlqName);
                dlqCount = (int)dlqOk.MessageCount;
                if (dlqCount >= 1) break;
            }

            Assert.Equal(1, dlqCount);

            var primaryOk = testChannel.QueueDeclarePassive(PrimaryQueueName);
            Assert.Equal(0u, primaryOk.MessageCount);

            using (var verifyScope = serviceProvider.CreateScope())
            {
                var db = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var pe = await db.ProcessedEvents.FindAsync(eventId, "GpsConsumer");
                Assert.Null(pe);

                var hist = await db.RiderLocationHistories.Where(h => h.RiderId == riderId).ToListAsync();
                Assert.Empty(hist);
            }

            bool seenDuringFailure = await realRedisDb.KeyExistsAsync("telemetry:stream:seen:" + eventId.ToString("D"));
            Assert.False(seenDuringFailure, "Redis seen guard key must not exist when write failed.");

            // =========================================================================
            // PHASE 5: Redis Recovery & DLQ Replay
            // =========================================================================
            simulateRedisFailure = false;

            var dlqDelivery = testChannel.BasicGet(DlqName, autoAck: false);
            Assert.NotNull(dlqDelivery);

            var replayProps = testChannel.CreateBasicProperties();
            replayProps.Persistent = true;
            testChannel.BasicPublish(
                exchange: "",
                routingKey: PrimaryQueueName,
                basicProperties: replayProps,
                body: dlqDelivery.Body
            );

            testChannel.BasicAck(dlqDelivery.DeliveryTag, multiple: false);

            // =========================================================================
            // PHASE 6: Verify Recovery: Successful Commit & ACK
            // =========================================================================
            bool committed = false;
            for (int i = 0; i < 60; i++)
            {
                await Task.Delay(100);
                using var pollScope = serviceProvider.CreateScope();
                var pollDb = pollScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                if (await pollDb.ProcessedEvents.AnyAsync(p => p.EventId == eventId && p.HandlerName == "GpsConsumer"))
                {
                    committed = true;
                    break;
                }
            }

            Assert.True(committed, "Replayed message from DLQ must be successfully processed and committed to PostgreSQL upon Redis recovery.");

            using (var finalScope = serviceProvider.CreateScope())
            {
                var db = finalScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var finalPe = await db.ProcessedEvents.FindAsync(eventId, "GpsConsumer");
                Assert.NotNull(finalPe);
                Assert.Equal("GpsConsumer", finalPe.HandlerName);

                var finalHist = await db.RiderLocationHistories.Where(h => h.RiderId == riderId).ToListAsync();
                Assert.Single(finalHist);
                Assert.Equal(13.7563, finalHist[0].Location.Y, 4);
                Assert.Equal(100.5018, finalHist[0].Location.X, 4);
            }

            bool seenAfterRecovery = await realRedisDb.KeyExistsAsync("telemetry:stream:seen:" + eventId.ToString("D"));
            Assert.True(seenAfterRecovery, "Redis seen guard key must exist after successful commit.");

            var streamEntries = await realRedisDb.StreamRangeAsync("telemetry:stream:gps", "-", "+", count: 20, messageOrder: StackExchange.Redis.Order.Descending);
            var foundEntry = streamEntries.FirstOrDefault(e => e.Values.Any(v => v.Name == "eventId" && v.Value == eventId.ToString("D")));
            Assert.True(foundEntry.Id.HasValue, "Event must be present in Redis stream telemetry:stream:gps after recovery.");

            var finalPrimaryOk = testChannel.QueueDeclarePassive(PrimaryQueueName);
            Assert.Equal(0u, finalPrimaryOk.MessageCount);

            var finalDlqOk = testChannel.QueueDeclarePassive(DlqName);
            Assert.Equal(0u, finalDlqOk.MessageCount);

            // =========================================================================
            // PHASE 7: Teardown & Clean Shutdown
            // =========================================================================
            await worker.StopAsync(CancellationToken.None);

            using (var cleanScope = serviceProvider.CreateScope())
            {
                var db = cleanScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var pe = await db.ProcessedEvents.FindAsync(eventId, "GpsConsumer");
                if (pe != null) db.ProcessedEvents.Remove(pe);
                var hist = await db.RiderLocationHistories.Where(h => h.RiderId == riderId).ToListAsync();
                if (hist.Count > 0) db.RiderLocationHistories.RemoveRange(hist);
                await db.SaveChangesAsync();
            }
            await realRedisDb.KeyDeleteAsync("telemetry:stream:seen:" + eventId.ToString("D"));
        }

        [Fact]
        public async Task SubStep_2_1_D_B_RedisSucceedsDbCommitFails_OrphanRedisEntryAndMinioArchiverDedup()
        {
            // =========================================================================
            // PHASE 1: Baseline Setup & Pre-conditions
            // =========================================================================
            var realRedis = await ConnectionMultiplexer.ConnectAsync(RedisConnectionString);
            var realRedisDb = realRedis.GetDatabase();

            var minioConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Minio:Endpoint"] = MinioEndpoint,
                    ["Minio:AccessKey"] = MinioAccessKey,
                    ["Minio:SecretKey"] = MinioSecretKey,
                    ["Minio:UseSsl"] = "false",
                    ["Minio:TelemetryBucketName"] = ArchiveBucketName
                })
                .Build();

            var storage = new MinioTelemetryArchiveStorage(minioConfig, new NullLogger<MinioTelemetryArchiveStorage>());
            await storage.EnsureArchiveBucketExistsAsync();

            var rabbitFactory = new ConnectionFactory
            {
                HostName = RabbitHost,
                Port = RabbitPort,
                UserName = RabbitUser,
                Password = RabbitPass,
                DispatchConsumersAsync = true
            };
            using var testRabbitConn = rabbitFactory.CreateConnection();
            using var testChannel = testRabbitConn.CreateModel();

            testChannel.QueuePurge(PrimaryQueueName);
            testChannel.QueuePurge(DlqName);

            var riderId = "rider-db-" + Guid.NewGuid().ToString("N")[..8];
            var timestamp = DateTime.UtcNow;
            var eventId = ComputeDeterministicGuid(riderId, timestamp.Ticks);
            var eventIdStr = eventId.ToString("D");

            // Register EF Core Interceptor to fail CommitAsync specifically
            var commitInterceptor = new FailCommitTransactionInterceptor { ShouldFailCommit = true };

            var services = new ServiceCollection();
            services.AddDbContext<ApplicationDbContext>(options =>
            {
                options.UseNpgsql(PgConnectionString, o => o.UseNetTopologySuite());
                options.AddInterceptors(commitInterceptor);
            });
            services.AddScoped<ICurrentUserService, TestCurrentUserService>();
            services.AddScoped<GpsHistoryService>();
            services.AddLogging();
            var serviceProvider = services.BuildServiceProvider();

            // Baseline clean: PG, Redis seen key, and Archiver seen key
            using (var initScope = serviceProvider.CreateScope())
            {
                var db = initScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var existingPe = await db.ProcessedEvents.FindAsync(eventId, "GpsConsumer");
                if (existingPe != null) db.ProcessedEvents.Remove(existingPe);

                var existingHist = await db.RiderLocationHistories.Where(h => h.RiderId == riderId).ToListAsync();
                if (existingHist.Count > 0) db.RiderLocationHistories.RemoveRange(existingHist);

                await db.SaveChangesAsync();
            }

            await realRedisDb.KeyDeleteAsync("telemetry:stream:seen:" + eventIdStr);
            await realRedisDb.KeyDeleteAsync(TelemetryArchiveWorker.SeenPrefix + eventIdStr);

            // Record baseline stream checkpoint cursor right before test entries
            var streamInfo = await realRedisDb.StreamInfoAsync(TelemetryArchiveWorker.StreamKey);
            var baselineCursor = streamInfo.LastGeneratedId.ToString();
            if (string.IsNullOrWhiteSpace(baselineCursor) || baselineCursor == "0-0")
            {
                baselineCursor = "0-0";
            }
            await realRedisDb.StringSetAsync(TelemetryArchiveWorker.CheckpointKey, baselineCursor);

            // =========================================================================
            // PHASE 2: Execution with Non-2PC Partial Failure (Redis OK, DB Commit Fails)
            // =========================================================================
            commitInterceptor.ShouldFailCommit = true;

            var workerConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["MessageBroker:Host"] = RabbitHost,
                    ["MessageBroker:Port"] = RabbitPort.ToString(),
                    ["MessageBroker:Username"] = RabbitUser,
                    ["MessageBroker:Password"] = RabbitPass
                })
                .Build();

            var lifetime = new TestHostLifetime();
            var worker = new GpsRabbitMqConsumerWorker(
                serviceProvider,
                workerConfig,
                lifetime,
                LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug)).CreateLogger<GpsRabbitMqConsumerWorker>(),
                realRedis
            );

            using var workerCts = new CancellationTokenSource();
            var workerTask = worker.StartAsync(workerCts.Token);
            await Task.Delay(1000);

            // Publish message
            var point = new TrackPoint(riderId, 13.7563, 100.5018, timestamp);
            var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(point));
            var props = testChannel.CreateBasicProperties();
            props.Persistent = true;
            testChannel.BasicPublish(exchange: "", routingKey: PrimaryQueueName, basicProperties: props, body: payload);

            // =========================================================================
            // PHASE 3: Verify Non-2PC Reality: Orphan Entry in Redis, PG Rolled Back, Message in DLQ
            // =========================================================================
            int dlqCount = 0;
            for (int i = 0; i < 60; i++)
            {
                await Task.Delay(100);
                var dlqOk = testChannel.QueueDeclarePassive(DlqName);
                dlqCount = (int)dlqOk.MessageCount;
                if (dlqCount >= 1) break;
            }

            Assert.Equal(1, dlqCount);

            // 1. PostgreSQL Invariant: 0 rows committed
            using (var verifyScope = serviceProvider.CreateScope())
            {
                var db = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var pe = await db.ProcessedEvents.FindAsync(eventId, "GpsConsumer");
                Assert.Null(pe); // Rolled back due to commit failure!

                var hist = await db.RiderLocationHistories.Where(h => h.RiderId == riderId).ToListAsync();
                Assert.Empty(hist); // 0 rows in PG!
            }

            // 2. Redis Invariant: Exactly 1 Orphan Entry exists in Stream!
            var orphanEntries = await realRedisDb.StreamRangeAsync(TelemetryArchiveWorker.StreamKey, baselineCursor, "+");
            var orphanMatches = orphanEntries.Where(e => e.Values.Any(v => v.Name == "eventId" && v.Value == eventIdStr)).ToList();
            Assert.Single(orphanMatches); // PROOF: Orphan Redis Stream entry physically exists!
            var orphanStreamId = orphanMatches[0].Id.ToString();

            // 3. RabbitMQ Invariant: Primary queue is 0, DLQ has 1
            var primaryCheck = testChannel.QueueDeclarePassive(PrimaryQueueName);
            Assert.Equal(0u, primaryCheck.MessageCount);

            // =========================================================================
            // PHASE 4: DLQ Replay & Recovery (Creates Duplicate in Redis Buffer)
            // =========================================================================
            // Restore DB Commit health
            commitInterceptor.ShouldFailCommit = false;

            // Replay message from DLQ to Primary Queue
            var dlqDelivery = testChannel.BasicGet(DlqName, autoAck: false);
            Assert.NotNull(dlqDelivery);

            var replayProps = testChannel.CreateBasicProperties();
            replayProps.Persistent = true;
            testChannel.BasicPublish(exchange: "", routingKey: PrimaryQueueName, basicProperties: replayProps, body: dlqDelivery.Body);
            testChannel.BasicAck(dlqDelivery.DeliveryTag, multiple: false);

            // Poll PostgreSQL until committed
            bool replayCommitted = false;
            for (int i = 0; i < 60; i++)
            {
                await Task.Delay(100);
                using var pollScope = serviceProvider.CreateScope();
                var pollDb = pollScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                if (await pollDb.ProcessedEvents.AnyAsync(p => p.EventId == eventId && p.HandlerName == "GpsConsumer"))
                {
                    replayCommitted = true;
                    break;
                }
            }

            Assert.True(replayCommitted, "DLQ replay must commit to PostgreSQL once commit failure is resolved.");

            // 1. PostgreSQL Authoritative State: Exactly 1 row
            using (var finalScope = serviceProvider.CreateScope())
            {
                var db = finalScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var finalPe = await db.ProcessedEvents.FindAsync(eventId, "GpsConsumer");
                Assert.NotNull(finalPe);

                var finalHist = await db.RiderLocationHistories.Where(h => h.RiderId == riderId).ToListAsync();
                Assert.Single(finalHist);
                Assert.Equal(13.7563, finalHist[0].Location.Y, 4);
                Assert.Equal(100.5018, finalHist[0].Location.X, 4);
            }

            // 2. Redis Stream Duplicate Proof: Stream now contains 2 entries with the SAME eventId!
            var currentStreamEntries = await realRedisDb.StreamRangeAsync(TelemetryArchiveWorker.StreamKey, baselineCursor, "+");
            var duplicateMatches = currentStreamEntries.Where(e => e.Values.Any(v => v.Name == "eventId" && v.Value == eventIdStr)).ToList();
            Assert.Equal(2, duplicateMatches.Count); // PROOF: Orphan (entry #1) + Replay (entry #2) in stream!
            Assert.NotEqual(duplicateMatches[0].Id, duplicateMatches[1].Id);

            // 3. RabbitMQ queues clean
            var postReplayPrimary = testChannel.QueueDeclarePassive(PrimaryQueueName);
            Assert.Equal(0u, postReplayPrimary.MessageCount);
            var postReplayDlq = testChannel.QueueDeclarePassive(DlqName);
            Assert.Equal(0u, postReplayDlq.MessageCount);

            // Stop consumer worker before archiving
            await worker.StopAsync(CancellationToken.None);

            // =========================================================================
            // PHASE 5: Downstream Phase 2.2 Archiver Dedup & MinIO Immutable Verification
            // =========================================================================
            // Clean any archiver seen keys for this event to simulate pristine cold archiver batch
            await realRedisDb.KeyDeleteAsync(TelemetryArchiveWorker.SeenPrefix + eventIdStr);

            var archiver = new TelemetryArchiveWorker(
                realRedis,
                storage,
                NullLogger<TelemetryArchiveWorker>.Instance,
                maxBatchEntries: 2,
                maxWaitInterval: TimeSpan.FromMilliseconds(200)
            );

            await archiver.InitializeCheckpointAsync();

            // Collect batch: Must include both stream entries (since cursor was at baselineCursor)
            var batch = await archiver.CollectBatchAsync();
            if (batch == null)
            {
                await Task.Delay(300);
                batch = await archiver.CollectBatchAsync();
            }
            Assert.NotNull(batch);

            // Invariant: RawEntries contains both duplicate stream entries
            var batchRawMatches = batch.RawEntries.Where(e => TelemetryArchiveWorker.ExtractEventId(e) == eventId).ToList();
            Assert.Equal(2, batchRawMatches.Count);

            // Invariant: DeduplicatedEntries contains EXACTLY 1 unique record (In-batch dedup applied!)
            var batchDedupMatches = batch.DeduplicatedEntries.Where(e => TelemetryArchiveWorker.ExtractEventId(e) == eventId).ToList();
            Assert.Single(batchDedupMatches);

            // Execute Archiver Pipeline commit (stages pending, uploads MinIO, commits checkpoint, deletes pending)
            await archiver.ProcessBatchPipelineAsync(batch);

            // Verify MinIO Cold Storage: Download and verify payload
            var objectKey = batch.GenerateObjectKey();
            byte[] objBytes = Array.Empty<byte>();
            await storage.GetArchiveObjectAsync(objectKey, s =>
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
            var lines = text.TrimEnd('\n', '\r').Split('\n', StringSplitOptions.RemoveEmptyEntries);

            var archivedPoints = new List<TelemetryArchivePoint>();
            foreach (var line in lines)
            {
                var pt = JsonSerializer.Deserialize<TelemetryArchivePoint>(line);
                if (pt != null) archivedPoints.Add(pt);
            }

            // CRITICAL CONTRACT PROOF: In MinIO cold storage, exactly 1 unique record exists for this eventId!
            var matchingArchived = archivedPoints.Where(p => p.EventId == eventIdStr).ToList();
            Assert.Single(matchingArchived);
            Assert.Equal(riderId, matchingArchived[0].RiderId);
            Assert.Equal(13.7563, matchingArchived[0].Lat);
            Assert.Equal(100.5018, matchingArchived[0].Lng);

            // Final Invariant Check: 0 missing, 0 extra, 0 duplicate
            var duplicateCountInArchive = archivedPoints.GroupBy(p => p.EventId).Count(g => g.Count() > 1);
            Assert.Equal(0, duplicateCountInArchive);

            // =========================================================================
            // PHASE 6: Teardown & Clean Baseline
            // =========================================================================
            using (var cleanScope = serviceProvider.CreateScope())
            {
                var db = cleanScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var pe = await db.ProcessedEvents.FindAsync(eventId, "GpsConsumer");
                if (pe != null) db.ProcessedEvents.Remove(pe);
                var hist = await db.RiderLocationHistories.Where(h => h.RiderId == riderId).ToListAsync();
                if (hist.Count > 0) db.RiderLocationHistories.RemoveRange(hist);
                await db.SaveChangesAsync();
            }
            await realRedisDb.KeyDeleteAsync("telemetry:stream:seen:" + eventIdStr);
            await realRedisDb.KeyDeleteAsync(TelemetryArchiveWorker.SeenPrefix + eventIdStr);
        }
    
        [Fact]
        public async Task SubStep_2_1_D_C_CrashBeforeRabbitMqAck_BrokerRedeliveryAndIdempotency()
        {
            // =========================================================================
            // PHASE 1: Baseline Setup & Pre-conditions
            // =========================================================================
            var realRedis = await ConnectionMultiplexer.ConnectAsync(RedisConnectionString);
            var realRedisDb = realRedis.GetDatabase();

            var rabbitFactory = new ConnectionFactory
            {
                HostName = RabbitHost,
                Port = RabbitPort,
                UserName = RabbitUser,
                Password = RabbitPass,
                DispatchConsumersAsync = true
            };
            using var testRabbitConn = rabbitFactory.CreateConnection();
            using var testChannel = testRabbitConn.CreateModel();

            testChannel.QueuePurge(PrimaryQueueName);
            testChannel.QueuePurge(DlqName);

            var riderId = "rider-dc-" + Guid.NewGuid().ToString("N")[..8];
            var timestamp = DateTime.UtcNow;
            var eventId = ComputeDeterministicGuid(riderId, timestamp.Ticks);
            var eventIdStr = eventId.ToString("D");

            var services = new ServiceCollection();
            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseNpgsql(PgConnectionString, o => o.UseNetTopologySuite()));
            services.AddScoped<ICurrentUserService, TestCurrentUserService>();
            services.AddScoped<GpsHistoryService>();
            services.AddLogging();
            var serviceProvider = services.BuildServiceProvider();

            using (var initScope = serviceProvider.CreateScope())
            {
                var db = initScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var existingPe = await db.ProcessedEvents.FindAsync(eventId, "GpsConsumer");
                if (existingPe != null) db.ProcessedEvents.Remove(existingPe);

                var existingHist = await db.RiderLocationHistories.Where(h => h.RiderId == riderId).ToListAsync();
                if (existingHist.Count > 0) db.RiderLocationHistories.RemoveRange(existingHist);

                await db.SaveChangesAsync();
            }

            await realRedisDb.KeyDeleteAsync("telemetry:stream:seen:" + eventIdStr);

            // Pre-condition assertion: System is at clean baseline
            using (var verifyScope = serviceProvider.CreateScope())
            {
                var db = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                Assert.Null(await db.ProcessedEvents.FindAsync(eventId, "GpsConsumer"));
                Assert.Empty(await db.RiderLocationHistories.Where(h => h.RiderId == riderId).ToListAsync());
            }
            Assert.False(await realRedisDb.KeyExistsAsync("telemetry:stream:seen:" + eventIdStr));
            Assert.Equal(0u, testChannel.QueueDeclarePassive(PrimaryQueueName).MessageCount);
            Assert.Equal(0u, testChannel.QueueDeclarePassive(DlqName).MessageCount);

            // =========================================================================
            // PHASE 2: Start Worker 1 with Crash-Before-Ack Fault Injection
            // =========================================================================
            var workerConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["MessageBroker:Host"] = RabbitHost,
                    ["MessageBroker:Port"] = RabbitPort.ToString(),
                    ["MessageBroker:Username"] = RabbitUser,
                    ["MessageBroker:Password"] = RabbitPass
                })
                .Build();

            CrashBeforeAckChannelProxy? capturedChannelProxy = null;

            var worker1 = new CrashBeforeAckWorker(
                serviceProvider,
                workerConfig,
                new TestHostLifetime(),
                LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug)).CreateLogger<GpsRabbitMqConsumerWorker>(),
                realRedis,
                realConn =>
                {
                    return CreateConnectionProxy(realConn, realModel =>
                    {
                        return CreateCrashChannelProxy(realModel, out capturedChannelProxy);
                    });
                }
            );

            using var worker1Cts = new CancellationTokenSource();
            var worker1Task = worker1.StartAsync(worker1Cts.Token);

            // Wait for worker1 to connect and establish subscription
            await Task.Delay(1000);
            Assert.NotNull(capturedChannelProxy);

            // =========================================================================
            // PHASE 3: Publish GPS Event & Trigger Simulated Crash Before Ack
            // =========================================================================
            var point = new TrackPoint(riderId, 13.7563, 100.5018, timestamp);
            var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(point));

            var props = testChannel.CreateBasicProperties();
            props.Persistent = true;
            testChannel.BasicPublish(
                exchange: "",
                routingKey: PrimaryQueueName,
                basicProperties: props,
                body: payload
            );

            // Wait for crash trigger (timeout 5s)
            var crashHappened = await Task.WhenAny(
                capturedChannelProxy.CrashTriggeredTcs.Task,
                Task.Delay(5000)
            ) == capturedChannelProxy.CrashTriggeredTcs.Task;

            Assert.True(crashHappened, "Simulated crash before BasicAck should have been triggered within 5 seconds.");

            // Stop worker1 completely
            worker1Cts.Cancel();
            try { await worker1Task; } catch { }
            worker1.Dispose();

            // =========================================================================
            // PHASE 4: Verify Crash State: PG Committed, Redis Written, RabbitMQ Broker Redelivery
            // =========================================================================
            // 1. PostgreSQL state: committed (New unique = 1)
            using (var crashCheckScope = serviceProvider.CreateScope())
            {
                var db = crashCheckScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var pe = await db.ProcessedEvents.FindAsync(eventId, "GpsConsumer");
                Assert.NotNull(pe);

                var hist = await db.RiderLocationHistories.Where(h => h.RiderId == riderId).ToListAsync();
                Assert.Single(hist);
                Assert.Equal(riderId, hist[0].RiderId);
            }

            // 2. Redis state: Stream entry added and seen guard key created
            bool seenAfterCrash = await realRedisDb.KeyExistsAsync("telemetry:stream:seen:" + eventIdStr);
            Assert.True(seenAfterCrash, "Redis seen guard key must exist after first delivery commit.");

            var streamEntries = await realRedisDb.StreamRangeAsync(GpsRabbitMqConsumerWorker.StreamKey, "-", "+", count: 1000);
            var matchingStreamEntries = streamEntries.Where(e =>
                e.Values.Any(v => v.Name == "eventId" && v.Value == eventIdStr)).ToList();
            Assert.Single(matchingStreamEntries);

            // 3. RabbitMQ state: Message returned to Primary Queue by broker (redelivered), NOT in DLQ!
            uint primaryMsgCount = 0;
            for (int i = 0; i < 50; i++)
            {
                await Task.Delay(100);
                var primaryOk = testChannel.QueueDeclarePassive(PrimaryQueueName);
                primaryMsgCount = primaryOk.MessageCount;
                if (primaryMsgCount >= 1) break;
            }

            Assert.Equal(1u, primaryMsgCount);

            var dlqOk = testChannel.QueueDeclarePassive(DlqName);
            Assert.Equal(0u, dlqOk.MessageCount);

            // =========================================================================
            // PHASE 5: Start Clean Worker 2 (Restart/Redelivery Consumption)
            // =========================================================================
            var worker2 = new GpsRabbitMqConsumerWorker(
                serviceProvider,
                workerConfig,
                new TestHostLifetime(),
                LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug)).CreateLogger<GpsRabbitMqConsumerWorker>(),
                realRedis
            );

            using var worker2Cts = new CancellationTokenSource();
            var worker2Task = worker2.StartAsync(worker2Cts.Token);

            // Wait for worker2 to drain and ACK the redelivered message
            bool primaryDrained = false;
            for (int i = 0; i < 60; i++)
            {
                await Task.Delay(100);
                var primaryOk = testChannel.QueueDeclarePassive(PrimaryQueueName);
                if (primaryOk.MessageCount == 0)
                {
                    primaryDrained = true;
                    break;
                }
            }

            Assert.True(primaryDrained, "Worker 2 must successfully process redelivery and ACK message from primary queue.");

            // Stop worker2 cleanly
            worker2Cts.Cancel();
            try { await worker2Task; } catch { }
            worker2.Dispose();

            // =========================================================================
            // PHASE 6: Strict Invariant Verification (Zero PG Duplicates, Zero Redis Duplicates)
            // =========================================================================
            // 1. Queue states: Both queues empty
            Assert.Equal(0u, testChannel.QueueDeclarePassive(PrimaryQueueName).MessageCount);
            Assert.Equal(0u, testChannel.QueueDeclarePassive(DlqName).MessageCount);

            // 2. PostgreSQL state: Exactly 1 ProcessedEvent, exactly 1 RiderLocationHistory
            using (var finalScope = serviceProvider.CreateScope())
            {
                var db = finalScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var peCount = await db.ProcessedEvents.CountAsync(p => p.EventId == eventId && p.HandlerName == "GpsConsumer");
                Assert.Equal(1, peCount);

                var histCount = await db.RiderLocationHistories.CountAsync(h => h.RiderId == riderId);
                Assert.Equal(1, histCount);
            }

            // 3. Redis state: Exactly 1 Stream entry with this eventId (idempotency guard prevented second XADD!)
            var finalStreamEntries = await realRedisDb.StreamRangeAsync(GpsRabbitMqConsumerWorker.StreamKey, "-", "+", count: 1000);
            var finalMatchingEntries = finalStreamEntries.Where(e =>
                e.Values.Any(v => v.Name == "eventId" && v.Value == eventIdStr)).ToList();
            Assert.Single(finalMatchingEntries);

            // 4. Redis seen guard key still exists
            Assert.True(await realRedisDb.KeyExistsAsync("telemetry:stream:seen:" + eventIdStr));

            // =========================================================================
            // PHASE 7: Teardown & Clean Baseline
            // =========================================================================
            using (var cleanScope = serviceProvider.CreateScope())
            {
                var db = cleanScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var pe = await db.ProcessedEvents.FindAsync(eventId, "GpsConsumer");
                if (pe != null) db.ProcessedEvents.Remove(pe);
                var hist = await db.RiderLocationHistories.Where(h => h.RiderId == riderId).ToListAsync();
                if (hist.Count > 0) db.RiderLocationHistories.RemoveRange(hist);
                await db.SaveChangesAsync();
            }
            await realRedisDb.KeyDeleteAsync("telemetry:stream:seen:" + eventIdStr);
        }


        [Fact]
        public async Task SubStep_2_1_D_D_DuplicateStormAndSeenGuardDynamics()
        {
            // =========================================================================
            // PHASE 1: Baseline Setup & Pre-conditions
            // =========================================================================
            var realRedis = await ConnectionMultiplexer.ConnectAsync(RedisConnectionString);
            var realRedisDb = realRedis.GetDatabase();

            var rabbitFactory = new ConnectionFactory
            {
                HostName = RabbitHost,
                Port = RabbitPort,
                UserName = RabbitUser,
                Password = RabbitPass,
                DispatchConsumersAsync = true
            };
            using var testRabbitConn = rabbitFactory.CreateConnection();
            using var testChannel = testRabbitConn.CreateModel();

            testChannel.QueuePurge(PrimaryQueueName);
            testChannel.QueuePurge(DlqName);

            var services = new ServiceCollection();
            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseNpgsql(PgConnectionString, o => o.UseNetTopologySuite()));
            services.AddScoped<ICurrentUserService, TestCurrentUserService>();
            services.AddScoped<GpsHistoryService>();
            services.AddLogging();
            var serviceProvider = services.BuildServiceProvider();

            // Configurable simulated Redis failure flag for D-D.3
            bool simulateRedisFailure = false;

            var mockRedisDb = new Mock<IDatabase>();
            mockRedisDb.Setup(d => d.StreamAddAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<NameValueEntry[]>(),
                It.IsAny<RedisValue?>(),
                It.IsAny<long?>(),
                It.IsAny<bool>(),
                It.IsAny<long?>(),
                It.IsAny<StreamTrimMode>(),
                It.IsAny<CommandFlags>()
            )).Returns(new Func<RedisKey, NameValueEntry[], RedisValue?, long?, bool, long?, StreamTrimMode, CommandFlags, Task<RedisValue>>((key, entries, id, maxLen, approx, limit, trim, flags) =>
            {
                if (simulateRedisFailure)
                {
                    throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Simulated Redis outage during stream repair");
                }
                return realRedisDb.StreamAddAsync(key, entries, id, maxLen, approx, limit, trim, flags);
            }));

            mockRedisDb.Setup(d => d.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<Expiration>(),
                It.IsAny<ValueCondition>(),
                It.IsAny<CommandFlags>()
            )).Returns(new Func<RedisKey, RedisValue, Expiration, ValueCondition, CommandFlags, Task<bool>>((key, value, expiry, when, flags) =>
            {
                if (simulateRedisFailure)
                {
                    throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Simulated Redis outage during seen guard update");
                }
                return realRedisDb.StringSetAsync(key, value, expiry, when, flags);
            }));

            mockRedisDb.Setup(d => d.KeyExistsAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<CommandFlags>()
            )).Returns(new Func<RedisKey, CommandFlags, Task<bool>>((key, flags) =>
            {
                if (simulateRedisFailure)
                {
                    throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Simulated Redis outage during seen check");
                }
                return realRedisDb.KeyExistsAsync(key, flags);
            }));

            var mockRedis = new Mock<IConnectionMultiplexer>();
            mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object?>())).Returns(mockRedisDb.Object);

            var workerConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["MessageBroker:Host"] = RabbitHost,
                    ["MessageBroker:Port"] = RabbitPort.ToString(),
                    ["MessageBroker:Username"] = RabbitUser,
                    ["MessageBroker:Password"] = RabbitPass
                })
                .Build();

            var worker = new GpsRabbitMqConsumerWorker(
                serviceProvider,
                workerConfig,
                new TestHostLifetime(),
                LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug)).CreateLogger<GpsRabbitMqConsumerWorker>(),
                mockRedis.Object
            );

            using var workerCts = new CancellationTokenSource();
            var workerTask = worker.StartAsync(workerCts.Token);
            await Task.Delay(1000);

            try
            {
                // =========================================================================
                // CASE D-D.1: Duplicate Storm Absorbed (ProcessedEvents + Seen Guard Active)
                // =========================================================================
                var rider1 = "rider-dd1-" + Guid.NewGuid().ToString("N")[..8];
                var time1 = DateTime.UtcNow;
                var event1 = ComputeDeterministicGuid(rider1, time1.Ticks);
                var event1Str = event1.ToString("D");

                // Seed PostgreSQL with Event 1
                using (var scope = serviceProvider.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var histService = scope.ServiceProvider.GetRequiredService<GpsHistoryService>();
                    db.ProcessedEvents.Add(new BackendApi.Models.SystemModels.ProcessedEvent
                    {
                        EventId = event1,
                        HandlerName = "GpsConsumer",
                        ProcessedAt = DateTime.UtcNow
                    });
                    await db.SaveChangesAsync();
                    await histService.SavePointsAsync(new List<TrackPoint> { new TrackPoint(rider1, 13.75, 100.50, time1) });
                }

                // Seed Redis Stream & Seen Guard with Event 1
                await realRedisDb.StreamAddAsync(GpsRabbitMqConsumerWorker.StreamKey, new NameValueEntry[]
                {
                    new NameValueEntry("eventId", event1Str),
                    new NameValueEntry("riderId", rider1),
                    new NameValueEntry("timestamp", time1.ToString("O")),
                    new NameValueEntry("lat", 13.75),
                    new NameValueEntry("lng", 100.50),
                    new NameValueEntry("accuracy", 5.0)
                });
                await realRedisDb.StringSetAsync("telemetry:stream:seen:" + event1Str, "1", TimeSpan.FromHours(1));

                var initialStreamEntries1 = (await realRedisDb.StreamRangeAsync(GpsRabbitMqConsumerWorker.StreamKey, "-", "+"))
                    .Count(e => e.Values.Any(v => v.Name == "eventId" && v.Value == event1Str));
                Assert.Equal(1, initialStreamEntries1);

                // Publish a storm of 5 identical messages
                for (int i = 0; i < 5; i++)
                {
                    var point1 = new TrackPoint(rider1, 13.75, 100.50, time1);
                    var body1 = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(point1));
                    testChannel.BasicPublish(exchange: "", routingKey: PrimaryQueueName, basicProperties: null, body: body1);
                }

                // Wait for worker to drain all 5 duplicate messages
                bool d1Drained = false;
                for (int i = 0; i < 60; i++)
                {
                    await Task.Delay(100);
                    if (testChannel.QueueDeclarePassive(PrimaryQueueName).MessageCount == 0)
                    {
                        d1Drained = true;
                        break;
                    }
                }
                Assert.True(d1Drained, "Worker must consume and ACK the duplicate storm.");

                // Invariant checks for D-D.1:
                Assert.Equal(0u, testChannel.QueueDeclarePassive(PrimaryQueueName).MessageCount);
                Assert.Equal(0u, testChannel.QueueDeclarePassive(DlqName).MessageCount);

                using (var scope = serviceProvider.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    Assert.Equal(1, await db.ProcessedEvents.CountAsync(p => p.EventId == event1 && p.HandlerName == "GpsConsumer"));
                    Assert.Equal(1, await db.RiderLocationHistories.CountAsync(h => h.RiderId == rider1));
                }

                var finalStreamEntries1 = (await realRedisDb.StreamRangeAsync(GpsRabbitMqConsumerWorker.StreamKey, "-", "+"))
                    .Count(e => e.Values.Any(v => v.Name == "eventId" && v.Value == event1Str));
                Assert.Equal(1, finalStreamEntries1);
                Assert.True(await realRedisDb.KeyExistsAsync("telemetry:stream:seen:" + event1Str));

                // =========================================================================
                // CASE D-D.2: Stream Repair on Guard Expiration / Gap
                // =========================================================================
                var rider2 = "rider-dd2-" + Guid.NewGuid().ToString("N")[..8];
                var time2 = DateTime.UtcNow;
                var event2 = ComputeDeterministicGuid(rider2, time2.Ticks);
                var event2Str = event2.ToString("D");

                // Seed PostgreSQL with Event 2
                using (var scope = serviceProvider.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var histService = scope.ServiceProvider.GetRequiredService<GpsHistoryService>();
                    db.ProcessedEvents.Add(new BackendApi.Models.SystemModels.ProcessedEvent
                    {
                        EventId = event2,
                        HandlerName = "GpsConsumer",
                        ProcessedAt = DateTime.UtcNow
                    });
                    await db.SaveChangesAsync();
                    await histService.SavePointsAsync(new List<TrackPoint> { new TrackPoint(rider2, 13.76, 100.51, time2) });
                }

                // In Redis: Seen guard is EXPIRED / MISSING, and entry is not in stream
                await realRedisDb.KeyDeleteAsync("telemetry:stream:seen:" + event2Str);

                var streamEntriesBefore2 = (await realRedisDb.StreamRangeAsync(GpsRabbitMqConsumerWorker.StreamKey, "-", "+"))
                    .Count(e => e.Values.Any(v => v.Name == "eventId" && v.Value == event2Str));
                Assert.Equal(0, streamEntriesBefore2);

                // Publish redelivered message for Event 2
                var point2 = new TrackPoint(rider2, 13.76, 100.51, time2);
                var body2 = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(point2));
                testChannel.BasicPublish(exchange: "", routingKey: PrimaryQueueName, basicProperties: null, body: body2);

                // Wait for worker to consume and repair
                bool d2Drained = false;
                for (int i = 0; i < 60; i++)
                {
                    await Task.Delay(100);
                    if (testChannel.QueueDeclarePassive(PrimaryQueueName).MessageCount == 0)
                    {
                        d2Drained = true;
                        break;
                    }
                }
                Assert.True(d2Drained, "Worker must consume, repair stream, and ACK Event 2.");

                Assert.Equal(0u, testChannel.QueueDeclarePassive(PrimaryQueueName).MessageCount);
                Assert.Equal(0u, testChannel.QueueDeclarePassive(DlqName).MessageCount);

                using (var scope = serviceProvider.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    Assert.Equal(1, await db.ProcessedEvents.CountAsync(p => p.EventId == event2 && p.HandlerName == "GpsConsumer"));
                    Assert.Equal(1, await db.RiderLocationHistories.CountAsync(h => h.RiderId == rider2));
                }

                var streamEntriesAfter2 = (await realRedisDb.StreamRangeAsync(GpsRabbitMqConsumerWorker.StreamKey, "-", "+"))
                    .Count(e => e.Values.Any(v => v.Name == "eventId" && v.Value == event2Str));
                Assert.Equal(1, streamEntriesAfter2);
                Assert.True(await realRedisDb.KeyExistsAsync("telemetry:stream:seen:" + event2Str));

                // =========================================================================
                // CASE D-D.3: Repair Attempt Fails Under Redis Outage -> Route to DLQ
                // =========================================================================
                var rider3 = "rider-dd3-" + Guid.NewGuid().ToString("N")[..8];
                var time3 = DateTime.UtcNow;
                var event3 = ComputeDeterministicGuid(rider3, time3.Ticks);
                var event3Str = event3.ToString("D");

                using (var scope = serviceProvider.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var histService = scope.ServiceProvider.GetRequiredService<GpsHistoryService>();
                    db.ProcessedEvents.Add(new BackendApi.Models.SystemModels.ProcessedEvent
                    {
                        EventId = event3,
                        HandlerName = "GpsConsumer",
                        ProcessedAt = DateTime.UtcNow
                    });
                    await db.SaveChangesAsync();
                    await histService.SavePointsAsync(new List<TrackPoint> { new TrackPoint(rider3, 13.77, 100.52, time3) });
                }

                await realRedisDb.KeyDeleteAsync("telemetry:stream:seen:" + event3Str);

                // Simulate Redis Outage during duplicate seen check / repair
                simulateRedisFailure = true;

                var point3 = new TrackPoint(rider3, 13.77, 100.52, time3);
                var body3 = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(point3));
                testChannel.BasicPublish(exchange: "", routingKey: PrimaryQueueName, basicProperties: null, body: body3);

                bool dlqReceived3 = false;
                for (int i = 0; i < 60; i++)
                {
                    await Task.Delay(100);
                    if (testChannel.QueueDeclarePassive(DlqName).MessageCount >= 1)
                    {
                        dlqReceived3 = true;
                        break;
                    }
                }
                var pCount = testChannel.QueueDeclarePassive(PrimaryQueueName).MessageCount; var dCount = testChannel.QueueDeclarePassive(DlqName).MessageCount; Assert.True(dlqReceived3, $"Worker must NACK to DLQ. PrimaryMsgCount: {pCount}, DlqCount: {dCount}");

                Assert.Equal(0u, testChannel.QueueDeclarePassive(PrimaryQueueName).MessageCount);
                Assert.Equal(1u, testChannel.QueueDeclarePassive(DlqName).MessageCount);

                using (var scope = serviceProvider.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    Assert.Equal(1, await db.ProcessedEvents.CountAsync(p => p.EventId == event3 && p.HandlerName == "GpsConsumer"));
                    Assert.Equal(1, await db.RiderLocationHistories.CountAsync(h => h.RiderId == rider3));
                }

                simulateRedisFailure = false;
                testChannel.QueuePurge(DlqName);

                // =========================================================================
                // TEARDOWN
                // =========================================================================
                using (var cleanScope = serviceProvider.CreateScope())
                {
                    var db = cleanScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var pe1 = await db.ProcessedEvents.FindAsync(event1, "GpsConsumer");
                    if (pe1 != null) db.ProcessedEvents.Remove(pe1);
                    var pe2 = await db.ProcessedEvents.FindAsync(event2, "GpsConsumer");
                    if (pe2 != null) db.ProcessedEvents.Remove(pe2);
                    var pe3 = await db.ProcessedEvents.FindAsync(event3, "GpsConsumer");
                    if (pe3 != null) db.ProcessedEvents.Remove(pe3);

                    var hists = await db.RiderLocationHistories.Where(h => h.RiderId == rider1 || h.RiderId == rider2 || h.RiderId == rider3).ToListAsync();
                    if (hists.Count > 0) db.RiderLocationHistories.RemoveRange(hists);
                    await db.SaveChangesAsync();
                }

                await realRedisDb.KeyDeleteAsync("telemetry:stream:seen:" + event1Str);
                await realRedisDb.KeyDeleteAsync("telemetry:stream:seen:" + event2Str);
                await realRedisDb.KeyDeleteAsync("telemetry:stream:seen:" + event3Str);
            }
            finally
            {
                workerCts.Cancel();
                try { await workerTask; } catch { }
                worker.Dispose();
            }
        }

    }
}





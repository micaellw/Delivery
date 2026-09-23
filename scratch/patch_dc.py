import os
import sys

target_path = r"B:\Delivery\RootScripts\scripts.test\test\BackendApi.IntegrationTests\Telemetry\DualWriteFailureRecoveryIntegrationTests.cs"

with open(target_path, "r", encoding="utf-8") as f:
    content = f.read()

# 1. Ensure using System.Reflection;
if "using System.Reflection;" not in content:
    content = "using System.Reflection;\n" + content

# 2. Add classes before ComputeDeterministicGuid
classes_code = """
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
"""

guid_marker = "        private static Guid ComputeDeterministicGuid(string riderId, long ticks)"
if guid_marker not in content:
    print("ERROR: guid_marker not found")
    sys.exit(1)

content = content.replace(guid_marker, classes_code.strip("\r\n") + "\n\n" + guid_marker)

# 3. Add SubStep_2_1_D_C method before the final two braces
method_code = """
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
                NullLogger<GpsRabbitMqConsumerWorker>.Instance,
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
                e.Values.Any(v => v.Name == "EventId" && v.Value == eventIdStr)).ToList();
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
                NullLogger<GpsRabbitMqConsumerWorker>.Instance,
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
                e.Values.Any(v => v.Name == "EventId" && v.Value == eventIdStr)).ToList();
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
"""

last_brace = content.rfind("}")
second_last = content.rfind("}", 0, last_brace)
content = content[:second_last] + method_code + "\n    }\n}\n"

with open(target_path, "w", encoding="utf-8") as f:
    f.write(content)

print("SUCCESS: DualWriteFailureRecoveryIntegrationTests.cs updated successfully!")

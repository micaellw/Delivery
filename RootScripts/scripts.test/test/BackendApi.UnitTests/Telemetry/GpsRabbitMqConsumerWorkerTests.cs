using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using StackExchange.Redis;
using Xunit;
using BackendApi.Data;
using BackendApi.Models;
using BackendApi.Models.Entities;
using BackendApi.Models.SystemModels;
using BackendApi.Services;
using BackendApi.Services.Auth;
using BackendApi.Services.Notifications;
using BackendApi.Services.Orders;
using BackendApi.Features.FleetTracking.Telemetry;
using BackendApi.Services.Telemetry;

namespace BackendApi.UnitTests.Telemetry
{
    public class GpsRabbitMqConsumerWorkerTests
    {
        private readonly Mock<IServiceProvider> _serviceProviderMock;
        private readonly Mock<IServiceScopeFactory> _scopeFactoryMock;
        private readonly Mock<IServiceScope> _scopeMock;
        private readonly Mock<IServiceProvider> _scopeServiceProviderMock;
        
        private readonly Mock<IConfiguration> _configMock;
        private readonly Mock<IHostApplicationLifetime> _appLifetimeMock;
        private readonly Mock<ILogger<GpsRabbitMqConsumerWorker>> _loggerMock;
        private readonly Mock<IConnection> _connectionMock;
        private readonly Mock<IModel> _channelMock;
        private readonly Mock<GpsHistoryService> _gpsHistoryServiceMock;
        private readonly Mock<ApplicationDbContext> _dbContextMock;
        private readonly Mock<DatabaseFacade> _databaseFacadeMock;
        private readonly Mock<IDbContextTransaction> _dbTransactionMock;

        public GpsRabbitMqConsumerWorkerTests()
        {
            _serviceProviderMock = new Mock<IServiceProvider>();
            _scopeFactoryMock = new Mock<IServiceScopeFactory>();
            _scopeMock = new Mock<IServiceScope>();
            _scopeServiceProviderMock = new Mock<IServiceProvider>();
            
            _configMock = new Mock<IConfiguration>();
            _appLifetimeMock = new Mock<IHostApplicationLifetime>();
            _loggerMock = new Mock<ILogger<GpsRabbitMqConsumerWorker>>();
            _connectionMock = new Mock<IConnection>();
            _channelMock = new Mock<IModel>();
            
            // Mock GpsHistoryService by passing null dependencies (since we mock SavePointsAsync anyway)
            _gpsHistoryServiceMock = new Mock<GpsHistoryService>(null!, null!);

            // Setup DB Context with In-Memory Database and DatabaseFacade Mock for Transactions
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;

            var currentUserServiceMock = new Mock<ICurrentUserService>();
            currentUserServiceMock.Setup(u => u.UserId).Returns(Guid.NewGuid());
            currentUserServiceMock.Setup(u => u.UserName).Returns("System");

            _dbContextMock = new Mock<ApplicationDbContext>(options, currentUserServiceMock.Object) { CallBase = true };
            _databaseFacadeMock = new Mock<DatabaseFacade>(_dbContextMock.Object);
            _dbTransactionMock = new Mock<IDbContextTransaction>();

            _dbContextMock.Setup(c => c.Database).Returns(_databaseFacadeMock.Object);
            _databaseFacadeMock.Setup(d => d.BeginTransactionAsync(It.IsAny<CancellationToken>()))
                               .ReturnsAsync(_dbTransactionMock.Object);

            // Setup IConfiguration values
            _configMock.Setup(c => c["MessageBroker:Host"]).Returns("localhost");
            _configMock.Setup(c => c["MessageBroker:Port"]).Returns("5672");
            _configMock.Setup(c => c["MessageBroker:Username"]).Returns("test-user");
            _configMock.Setup(c => c["MessageBroker:Password"]).Returns("test-password");

            // Setup DI Scoping
            _serviceProviderMock
                .Setup(s => s.GetService(typeof(IServiceScopeFactory)))
                .Returns(_scopeFactoryMock.Object);

            _scopeFactoryMock
                .Setup(sf => sf.CreateScope())
                .Returns(_scopeMock.Object);

            _scopeMock
                .Setup(s => s.ServiceProvider)
                .Returns(_scopeServiceProviderMock.Object);

            _scopeServiceProviderMock
                .Setup(s => s.GetService(typeof(GpsHistoryService)))
                .Returns(_gpsHistoryServiceMock.Object);

            _scopeServiceProviderMock
                .Setup(s => s.GetService(typeof(ApplicationDbContext)))
                .Returns(_dbContextMock.Object);

            // Setup Connection and Channel
            _connectionMock.Setup(c => c.CreateModel()).Returns(_channelMock.Object);
            _connectionMock.Setup(c => c.IsOpen).Returns(true);
            _channelMock.Setup(c => c.IsOpen).Returns(true);
        }

        [Fact]
        public async Task Worker_Should_ProcessBatchAndAckOnlyOnSuccessfulDatabaseSave()
        {
            // Arrange
            AsyncEventingBasicConsumer? capturedConsumer = null;

            // Capture the consumer when BasicConsume is registered
            _channelMock.Setup(c => c.BasicConsume(
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<IDictionary<string, object>?>(),
                It.IsAny<IBasicConsumer>()
            )).Callback((string queue, bool autoAck, string consumerTag, bool noLocal, bool exclusive, IDictionary<string, object>? args, IBasicConsumer consumer) =>
            {
                capturedConsumer = consumer as AsyncEventingBasicConsumer;
            }).Returns("consumer_tag");

            var worker = new TestableGpsRabbitMqConsumerWorker(
                _serviceProviderMock.Object,
                _configMock.Object,
                _appLifetimeMock.Object,
                _loggerMock.Object,
                _connectionMock.Object
            );

            // Setup history service mock behavior
            var saveCompletionSource = new TaskCompletionSource<bool>();
            _gpsHistoryServiceMock.Setup(g => g.SavePointsAsync(
                It.IsAny<List<TrackPoint>>(),
                It.IsAny<CancellationToken>()
            )).Callback<List<TrackPoint>, CancellationToken>((points, ct) =>
            {
                saveCompletionSource.SetResult(true);
            }).Returns(Task.CompletedTask);

            // Capture critical logs to diagnose initialization failure
            Exception? loggedException = null;
            _loggerMock.Setup(x => x.Log(
                LogLevel.Critical,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()
            )).Callback(new Action<LogLevel, EventId, object, Exception?, object>((level, id, state, ex, formatter) =>
            {
                loggedException = ex;
            }));

            // Start background worker
            using var cts = new CancellationTokenSource();
            var runTask = worker.StartAsync(cts.Token);

            // Wait for worker to initialize and capture consumer (avoid asynchronous race condition)
            int retries = 0;
            while (capturedConsumer == null && retries < 20)
            {
                await Task.Delay(50);
                retries++;
            }

            // Assert that consumer registration happened
            if (capturedConsumer == null && loggedException != null)
            {
                throw new InvalidOperationException("Worker failed to initialize.", loggedException);
            }
            Assert.NotNull(capturedConsumer);

            // Act - Simulate sending a GPS point from RabbitMQ
            var point = new TrackPoint("rider_789", 13.0, 100.0, DateTime.UtcNow);
            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(point));

            // Raise the event on consumer
            ulong deliveryTag = 999;
            await capturedConsumer.HandleBasicDeliver(
                "consumer_tag",
                deliveryTag,
                redelivered: false,
                exchange: "",
                routingKey: "gps_telemetry_queue",
                properties: _channelMock.Object.CreateBasicProperties(),
                body: body
            );

            // Wait for DB Save to be triggered (with timeout)
            var dbSaved = await Task.WhenAny(saveCompletionSource.Task, Task.Delay(5000)) == saveCompletionSource.Task;
            Assert.True(dbSaved, "Database save was not triggered in time.");

            // Cancel worker execution loop
            await cts.CancelAsync();
            await runTask;

            // Assert - Check that DB Save points count is 1 and Ack was called with correct tag
            _gpsHistoryServiceMock.Verify(g => g.SavePointsAsync(
                It.Is<List<TrackPoint>>(list => list.Count == 1 && list[0].RiderId == "rider_789"),
                It.IsAny<CancellationToken>()
            ), Times.Once);

            _channelMock.Verify(c => c.BasicAck(
                deliveryTag,
                true // multiple: true
            ), Times.Once);
        }

        [Fact]
        public async Task Worker_WhenDatabaseSaveFails_ShouldNotAckMessages()
        {
            // Arrange
            AsyncEventingBasicConsumer? capturedConsumer = null;

            _channelMock.Setup(c => c.BasicConsume(
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<IDictionary<string, object>?>(),
                It.IsAny<IBasicConsumer>()
            )).Callback((string queue, bool autoAck, string consumerTag, bool noLocal, bool exclusive, IDictionary<string, object>? args, IBasicConsumer consumer) =>
            {
                capturedConsumer = consumer as AsyncEventingBasicConsumer;
            }).Returns("consumer_tag");

            var worker = new TestableGpsRabbitMqConsumerWorker(
                _serviceProviderMock.Object,
                _configMock.Object,
                _appLifetimeMock.Object,
                _loggerMock.Object,
                _connectionMock.Object
            );

            // Simulate DB failure
            var dbFailCompletion = new TaskCompletionSource<bool>();
            _gpsHistoryServiceMock.Setup(g => g.SavePointsAsync(
                It.IsAny<List<TrackPoint>>(),
                It.IsAny<CancellationToken>()
            )).Callback<List<TrackPoint>, CancellationToken>((points, ct) =>
            {
                dbFailCompletion.SetResult(true);
            }).ThrowsAsync(new Exception("Database connection failure!"));

            using var cts = new CancellationTokenSource();
            var runTask = worker.StartAsync(cts.Token);

            // Wait for worker to initialize and capture consumer (avoid asynchronous race condition)
            int retries = 0;
            while (capturedConsumer == null && retries < 20)
            {
                await Task.Delay(50);
                retries++;
            }

            Assert.NotNull(capturedConsumer);

            var point = new TrackPoint("rider_fail", 14.0, 101.0, DateTime.UtcNow);
            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(point));

            ulong deliveryTag = 888;
            await capturedConsumer.HandleBasicDeliver(
                "consumer_tag",
                deliveryTag,
                redelivered: false,
                exchange: "",
                routingKey: "gps_telemetry_queue",
                properties: _channelMock.Object.CreateBasicProperties(),
                body: body
            );

            // Wait for DB Save attempt
            var dbAttempted = await Task.WhenAny(dbFailCompletion.Task, Task.Delay(5000)) == dbFailCompletion.Task;
            Assert.True(dbAttempted);

            await cts.CancelAsync();
            await runTask;

            // Assert - Verify DB Save was attempted but NO BasicAck was called due to the crash
            _gpsHistoryServiceMock.Verify(g => g.SavePointsAsync(It.IsAny<List<TrackPoint>>(), It.IsAny<CancellationToken>()), Times.Once);
            
            _channelMock.Verify(c => c.BasicAck(
                It.IsAny<ulong>(),
                It.IsAny<bool>()
            ), Times.Never);
        }

        [Fact]
        public async Task Worker_DualWrite_BothSucceed_WritesBothSinksAndAcksRabbitMq()
        {
            // Arrange
            AsyncEventingBasicConsumer? capturedConsumer = null;
            _channelMock.Setup(c => c.BasicConsume(
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<bool>(), It.IsAny<IDictionary<string, object>?>(), It.IsAny<IBasicConsumer>()
            )).Callback((string q, bool a, string t, bool nl, bool ex, IDictionary<string, object>? args, IBasicConsumer c) =>
            {
                capturedConsumer = c as AsyncEventingBasicConsumer;
            }).Returns("consumer_tag");

            var mockRedis = new Mock<IConnectionMultiplexer>();
            var mockRedisDb = new Mock<StackExchange.Redis.IDatabase>();
            mockRedis.SetReturnsDefault<StackExchange.Redis.IDatabase>(mockRedisDb.Object);
            mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object?>())).Returns(mockRedisDb.Object);

            mockRedisDb.SetReturnsDefault<Task<RedisValue>>(Task.FromResult((RedisValue)"1-0"));
            mockRedisDb.SetReturnsDefault<Task<bool>>(Task.FromResult(true));

            var ackTriggered = new TaskCompletionSource<bool>();
            _channelMock.Setup(c => c.BasicAck(It.IsAny<ulong>(), It.IsAny<bool>()))
                .Callback((ulong tag, bool multiple) => ackTriggered.TrySetResult(true));

            var worker = new TestableGpsRabbitMqConsumerWorker(
                _serviceProviderMock.Object, _configMock.Object, _appLifetimeMock.Object,
                _loggerMock.Object, _connectionMock.Object, mockRedis.Object
            );

            using var cts = new CancellationTokenSource();
            var runTask = worker.StartAsync(cts.Token);

            int retries = 0;
            while (capturedConsumer == null && retries < 20)
            {
                await Task.Delay(50);
                retries++;
            }
            Assert.NotNull(capturedConsumer);

            var point = new TrackPoint("rider_dual_1", 13.75, 100.5, DateTime.UtcNow);
            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(point));
            ulong deliveryTag = 101;

            await capturedConsumer.HandleBasicDeliver(
                "consumer_tag", deliveryTag, false, "", "gps_telemetry_queue",
                _channelMock.Object.CreateBasicProperties(), body
            );

            var acked = await Task.WhenAny(ackTriggered.Task, Task.Delay(5000)) == ackTriggered.Task;
            Assert.True(acked, "RabbitMQ BasicAck was not triggered.");

            await cts.CancelAsync();
            await runTask;

            // Assert: DB save executed
            _gpsHistoryServiceMock.Verify(g => g.SavePointsAsync(It.IsAny<List<TrackPoint>>(), It.IsAny<CancellationToken>()), Times.Once);
            // Assert: Redis StreamAdd executed
            mockRedisDb.Verify(d => d.StreamAddAsync(
                It.Is<RedisKey>(k => k == GpsRabbitMqConsumerWorker.StreamKey),
                It.IsAny<NameValueEntry[]>(),
                It.IsAny<RedisValue?>(),
                It.Is<long?>(l => l == GpsRabbitMqConsumerWorker.StreamMaxLen),
                true,
                It.IsAny<long?>(),
                It.IsAny<StreamTrimMode>(),
                It.IsAny<CommandFlags>()
            ), Times.Once);
            // Assert: RabbitMQ ACKed with multiple: true
            _channelMock.Verify(c => c.BasicAck(deliveryTag, true), Times.Once);
            _channelMock.Verify(c => c.BasicNack(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never);
        }

        [Fact]
        public async Task Worker_DualWrite_RedisFails_RollsBackDbAndNacks()
        {
            // Arrange
            AsyncEventingBasicConsumer? capturedConsumer = null;
            _channelMock.Setup(c => c.BasicConsume(
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<bool>(), It.IsAny<IDictionary<string, object>?>(), It.IsAny<IBasicConsumer>()
            )).Callback((string q, bool a, string t, bool nl, bool ex, IDictionary<string, object>? args, IBasicConsumer c) =>
            {
                capturedConsumer = c as AsyncEventingBasicConsumer;
            }).Returns("consumer_tag");

            var mockRedis = new Mock<IConnectionMultiplexer>();
            var mockRedisDb = new Mock<StackExchange.Redis.IDatabase>();
            mockRedis.SetReturnsDefault<StackExchange.Redis.IDatabase>(mockRedisDb.Object);
            mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object?>())).Returns(mockRedisDb.Object);
            mockRedisDb.SetReturnsDefault<Task<bool>>(Task.FromResult(true));

            mockRedisDb.Setup(d => d.StreamAddAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<NameValueEntry[]>(),
                It.IsAny<RedisValue?>(),
                It.IsAny<long?>(),
                It.IsAny<bool>(),
                It.IsAny<long?>(),
                It.IsAny<StreamTrimMode>(),
                It.IsAny<CommandFlags>()
            )).ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Redis connection failed!"));

            var nackTriggered = new TaskCompletionSource<bool>();
            _channelMock.Setup(c => c.BasicNack(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<bool>()))
                .Callback((ulong tag, bool multiple, bool requeue) => nackTriggered.TrySetResult(true));

            var worker = new TestableGpsRabbitMqConsumerWorker(
                _serviceProviderMock.Object, _configMock.Object, _appLifetimeMock.Object,
                _loggerMock.Object, _connectionMock.Object, mockRedis.Object
            );

            using var cts = new CancellationTokenSource();
            var runTask = worker.StartAsync(cts.Token);

            int retries = 0;
            while (capturedConsumer == null && retries < 20)
            {
                await Task.Delay(50);
                retries++;
            }
            Assert.NotNull(capturedConsumer);

            var point = new TrackPoint("rider_fail_redis", 13.80, 100.6, DateTime.UtcNow);
            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(point));
            ulong deliveryTag = 202;

            await capturedConsumer.HandleBasicDeliver(
                "consumer_tag", deliveryTag, false, "", "gps_telemetry_queue",
                _channelMock.Object.CreateBasicProperties(), body
            );

            var nacked = await Task.WhenAny(nackTriggered.Task, Task.Delay(5000)) == nackTriggered.Task;
            Assert.True(nacked, "BasicNack was not triggered.");

            await cts.CancelAsync();
            await runTask;

            // Assert: DB transaction rolled back
            _dbTransactionMock.Verify(t => t.RollbackAsync(It.IsAny<CancellationToken>()), Times.Once);
            // Assert: RabbitMQ NACKed with multiple: true, requeue: false (DLQ)
            _channelMock.Verify(c => c.BasicNack(deliveryTag, true, false), Times.Once);
            _channelMock.Verify(c => c.BasicAck(It.IsAny<ulong>(), It.IsAny<bool>()), Times.Never);
        }

        [Fact]
        public async Task Worker_DualWrite_DbDuplicate_SeenExists_SkipsStreamWriteAndAcks()
        {
            // Arrange
            AsyncEventingBasicConsumer? capturedConsumer = null;
            _channelMock.Setup(c => c.BasicConsume(
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<bool>(), It.IsAny<IDictionary<string, object>?>(), It.IsAny<IBasicConsumer>()
            )).Callback((string q, bool a, string t, bool nl, bool ex, IDictionary<string, object>? args, IBasicConsumer c) =>
            {
                capturedConsumer = c as AsyncEventingBasicConsumer;
            }).Returns("consumer_tag");

            var mockRedis = new Mock<IConnectionMultiplexer>();
            var mockRedisDb = new Mock<StackExchange.Redis.IDatabase>();
            mockRedis.SetReturnsDefault<StackExchange.Redis.IDatabase>(mockRedisDb.Object);
            mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object?>())).Returns(mockRedisDb.Object);
            mockRedisDb.SetReturnsDefault<Task<bool>>(Task.FromResult(true)); // Seen key exists!

            var ackTriggered = new TaskCompletionSource<bool>();
            _channelMock.Setup(c => c.BasicAck(It.IsAny<ulong>(), It.IsAny<bool>()))
                .Callback((ulong tag, bool multiple) => ackTriggered.TrySetResult(true));

            var worker = new TestableGpsRabbitMqConsumerWorker(
                _serviceProviderMock.Object, _configMock.Object, _appLifetimeMock.Object,
                _loggerMock.Object, _connectionMock.Object, mockRedis.Object
            );

            // Pre-seed ProcessedEvents in DbContext to simulate DB duplicate
            var point = new TrackPoint("rider_dup_seen", 13.85, 100.7, new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc));
            string key = $"{point.RiderId}_{point.Timestamp.Ticks}";
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            var existingEventId = new Guid(hash.AsSpan(0, 16));

            _dbContextMock.Object.ProcessedEvents.Add(new ProcessedEvent
            {
                EventId = existingEventId,
                HandlerName = "GpsConsumer",
                ProcessedAt = DateTime.UtcNow.AddMinutes(-5)
            });
            await _dbContextMock.Object.SaveChangesAsync();

            using var cts = new CancellationTokenSource();
            var runTask = worker.StartAsync(cts.Token);

            int retries = 0;
            while (capturedConsumer == null && retries < 20)
            {
                await Task.Delay(50);
                retries++;
            }
            Assert.NotNull(capturedConsumer);

            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(point));
            ulong deliveryTag = 303;

            await capturedConsumer.HandleBasicDeliver(
                "consumer_tag", deliveryTag, true, "", "gps_telemetry_queue",
                _channelMock.Object.CreateBasicProperties(), body
            );

            var acked = await Task.WhenAny(ackTriggered.Task, Task.Delay(5000)) == ackTriggered.Task;
            Assert.True(acked, "RabbitMQ BasicAck was not triggered.");

            await cts.CancelAsync();
            await runTask;

            // Verify: DB save skipped, Redis StreamAdd skipped, and RabbitMQ ACKed
            _gpsHistoryServiceMock.Verify(g => g.SavePointsAsync(It.IsAny<List<TrackPoint>>(), It.IsAny<CancellationToken>()), Times.Never);
            mockRedisDb.Verify(d => d.StreamAddAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<NameValueEntry[]>(),
                It.IsAny<RedisValue?>(),
                It.IsAny<long?>(),
                It.IsAny<bool>(),
                It.IsAny<long?>(),
                It.IsAny<StreamTrimMode>(),
                It.IsAny<CommandFlags>()
            ), Times.Never);
            _channelMock.Verify(c => c.BasicAck(deliveryTag, true), Times.Once);
            _channelMock.Verify(c => c.BasicNack(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never);
        }

        [Fact]
        public async Task Worker_DualWrite_DbDuplicate_SeenMissing_RepairsStreamAndAcks()
        {
            // Arrange
            AsyncEventingBasicConsumer? capturedConsumer = null;
            _channelMock.Setup(c => c.BasicConsume(
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<bool>(), It.IsAny<IDictionary<string, object>?>(), It.IsAny<IBasicConsumer>()
            )).Callback((string q, bool a, string t, bool nl, bool ex, IDictionary<string, object>? args, IBasicConsumer c) =>
            {
                capturedConsumer = c as AsyncEventingBasicConsumer;
            }).Returns("consumer_tag");

            var mockRedis = new Mock<IConnectionMultiplexer>();
            var mockRedisDb = new Mock<StackExchange.Redis.IDatabase>();
            mockRedis.SetReturnsDefault<StackExchange.Redis.IDatabase>(mockRedisDb.Object);
            mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object?>())).Returns(mockRedisDb.Object);
            mockRedisDb.SetReturnsDefault<Task<RedisValue>>(Task.FromResult((RedisValue)"2-0"));
            mockRedisDb.SetReturnsDefault<Task<bool>>(Task.FromResult(true));

            mockRedisDb.Setup(d => d.KeyExistsAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<CommandFlags>()
            )).ReturnsAsync(false); // Seen key missing -> requires repair!

            var ackTriggered = new TaskCompletionSource<bool>();
            _channelMock.Setup(c => c.BasicAck(It.IsAny<ulong>(), It.IsAny<bool>()))
                .Callback((ulong tag, bool multiple) => ackTriggered.TrySetResult(true));

            var worker = new TestableGpsRabbitMqConsumerWorker(
                _serviceProviderMock.Object, _configMock.Object, _appLifetimeMock.Object,
                _loggerMock.Object, _connectionMock.Object, mockRedis.Object
            );

            // Pre-seed ProcessedEvents in DbContext to simulate DB duplicate
            var point = new TrackPoint("rider_dup_missing", 13.90, 100.8, new DateTime(2026, 9, 20, 13, 0, 0, DateTimeKind.Utc));
            string key = $"{point.RiderId}_{point.Timestamp.Ticks}";
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            var existingEventId = new Guid(hash.AsSpan(0, 16));

            _dbContextMock.Object.ProcessedEvents.Add(new ProcessedEvent
            {
                EventId = existingEventId,
                HandlerName = "GpsConsumer",
                ProcessedAt = DateTime.UtcNow.AddMinutes(-5)
            });
            await _dbContextMock.Object.SaveChangesAsync();

            using var cts = new CancellationTokenSource();
            var runTask = worker.StartAsync(cts.Token);

            int retries = 0;
            while (capturedConsumer == null && retries < 20)
            {
                await Task.Delay(50);
                retries++;
            }
            Assert.NotNull(capturedConsumer);

            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(point));
            ulong deliveryTag = 404;

            await capturedConsumer.HandleBasicDeliver(
                "consumer_tag", deliveryTag, true, "", "gps_telemetry_queue",
                _channelMock.Object.CreateBasicProperties(), body
            );

            var acked = await Task.WhenAny(ackTriggered.Task, Task.Delay(5000)) == ackTriggered.Task;
            Assert.True(acked, "RabbitMQ BasicAck was not triggered.");

            await cts.CancelAsync();
            await runTask;

            // Verify: DB save skipped, Redis StreamAdd executed for repair, and RabbitMQ ACKed
            _gpsHistoryServiceMock.Verify(g => g.SavePointsAsync(It.IsAny<List<TrackPoint>>(), It.IsAny<CancellationToken>()), Times.Never);
            mockRedisDb.Verify(d => d.StreamAddAsync(
                It.Is<RedisKey>(k => k == GpsRabbitMqConsumerWorker.StreamKey),
                It.IsAny<NameValueEntry[]>(),
                It.IsAny<RedisValue?>(),
                It.Is<long?>(l => l == GpsRabbitMqConsumerWorker.StreamMaxLen),
                true,
                It.IsAny<long?>(),
                It.IsAny<StreamTrimMode>(),
                It.IsAny<CommandFlags>()
            ), Times.Once);
            _channelMock.Verify(c => c.BasicAck(deliveryTag, true), Times.Once);
            _channelMock.Verify(c => c.BasicNack(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never);
        }

        [Fact]
        public async Task Worker_DualWrite_DbDuplicate_RepairRedisFails_Nacks()
        {
            // Arrange
            AsyncEventingBasicConsumer? capturedConsumer = null;
            _channelMock.Setup(c => c.BasicConsume(
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<bool>(), It.IsAny<IDictionary<string, object>?>(), It.IsAny<IBasicConsumer>()
            )).Callback((string q, bool a, string t, bool nl, bool ex, IDictionary<string, object>? args, IBasicConsumer c) =>
            {
                capturedConsumer = c as AsyncEventingBasicConsumer;
            }).Returns("consumer_tag");

            var mockRedis = new Mock<IConnectionMultiplexer>();
            var mockRedisDb = new Mock<StackExchange.Redis.IDatabase>();
            mockRedis.SetReturnsDefault<StackExchange.Redis.IDatabase>(mockRedisDb.Object);
            mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object?>())).Returns(mockRedisDb.Object);
            mockRedisDb.SetReturnsDefault<Task<bool>>(Task.FromResult(true));

            mockRedisDb.Setup(d => d.KeyExistsAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<CommandFlags>()
            )).ReturnsAsync(false); // Seen missing -> tries repair

            mockRedisDb.Setup(d => d.StreamAddAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<NameValueEntry[]>(),
                It.IsAny<RedisValue?>(),
                It.IsAny<long?>(),
                It.IsAny<bool>(),
                It.IsAny<long?>(),
                It.IsAny<StreamTrimMode>(),
                It.IsAny<CommandFlags>()
            )).ThrowsAsync(new RedisTimeoutException("Redis timed out during repair!", CommandStatus.WaitingToBeSent));

            var nackTriggered = new TaskCompletionSource<bool>();
            _channelMock.Setup(c => c.BasicNack(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<bool>()))
                .Callback((ulong tag, bool multiple, bool requeue) => nackTriggered.TrySetResult(true));

            var worker = new TestableGpsRabbitMqConsumerWorker(
                _serviceProviderMock.Object, _configMock.Object, _appLifetimeMock.Object,
                _loggerMock.Object, _connectionMock.Object, mockRedis.Object
            );

            // Pre-seed ProcessedEvents in DbContext to simulate DB duplicate
            var point = new TrackPoint("rider_dup_repair_fail", 13.95, 100.9, new DateTime(2026, 9, 20, 14, 0, 0, DateTimeKind.Utc));
            string key = $"{point.RiderId}_{point.Timestamp.Ticks}";
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            var existingEventId = new Guid(hash.AsSpan(0, 16));

            _dbContextMock.Object.ProcessedEvents.Add(new ProcessedEvent
            {
                EventId = existingEventId,
                HandlerName = "GpsConsumer",
                ProcessedAt = DateTime.UtcNow.AddMinutes(-5)
            });
            await _dbContextMock.Object.SaveChangesAsync();

            using var cts = new CancellationTokenSource();
            var runTask = worker.StartAsync(cts.Token);

            int retries = 0;
            while (capturedConsumer == null && retries < 20)
            {
                await Task.Delay(50);
                retries++;
            }
            Assert.NotNull(capturedConsumer);

            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(point));
            ulong deliveryTag = 505;

            await capturedConsumer.HandleBasicDeliver(
                "consumer_tag", deliveryTag, true, "", "gps_telemetry_queue",
                _channelMock.Object.CreateBasicProperties(), body
            );

            var nacked = await Task.WhenAny(nackTriggered.Task, Task.Delay(5000)) == nackTriggered.Task;
            Assert.True(nacked, "BasicNack was not triggered.");

            await cts.CancelAsync();
            await runTask;

            // Verify: BasicAck is NEVER called, and BasicNack IS called (message not dropped, sent to DLQ)
            _channelMock.Verify(c => c.BasicAck(It.IsAny<ulong>(), It.IsAny<bool>()), Times.Never);
            _channelMock.Verify(c => c.BasicNack(deliveryTag, true, false), Times.Once);
        }

        // Subclass to inject Mock Connection and bypass actual socket creation
        private class TestableGpsRabbitMqConsumerWorker : GpsRabbitMqConsumerWorker
        {
            private readonly IConnection _mockConnection;

            public TestableGpsRabbitMqConsumerWorker(
                IServiceProvider serviceProvider,
                IConfiguration configuration,
                IHostApplicationLifetime appLifetime,
                ILogger<GpsRabbitMqConsumerWorker> logger,
                IConnection mockConnection,
                IConnectionMultiplexer? redis = null) : base(serviceProvider, configuration, appLifetime, logger, redis)
            {
                _mockConnection = mockConnection;
            }

            protected override IConnection CreateConnection(ConnectionFactory factory)
            {
                return _mockConnection;
            }
        }
    }
}

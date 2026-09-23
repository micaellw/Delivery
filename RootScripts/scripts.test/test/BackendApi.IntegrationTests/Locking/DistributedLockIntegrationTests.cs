using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using BackendApi.Data;
using BackendApi.Infrastructure.Redis;
using BackendApi.IntegrationTests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace BackendApi.IntegrationTests.Locking;

[Collection("SharedTestDatabase")]
public class DistributedLockIntegrationTests : IAsyncLifetime
{
    private readonly DeliveryWebApplicationFactory _factory;

    public DistributedLockIntegrationTests(DeliveryWebApplicationFactory factory)
    {
        _factory = factory;
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    private (RedisLockService Service, ApplicationDbContext DbContext, IConnectionMultiplexer Redis) CreateLiveLockService(IServiceScope scope)
    {
        var redis = scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<RedisLockService>>();
        var service = new RedisLockService(redis, logger, db);
        return (service, db, redis);
    }

    private (RedisLockService Service, ApplicationDbContext DbContext) CreateFailingRedisLockService(IServiceScope scope)
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<RedisLockService>>();

        var redisMock = new Mock<IConnectionMultiplexer>();
        redisMock.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Throws(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Simulated live Redis failure"));

        var service = new RedisLockService(redisMock.Object, logger, db);
        return (service, db);
    }

    // ─────────────────────────────────────────────────────────────────
    // Sub-step 3.1-A: Live Redis Distributed Lock
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SubStep_3_1_A1_LiveRedis_100ConcurrencyRace_ExactOneWinner()
    {
        using var scope = _factory.Services.CreateScope();
        var (lockService, _, redis) = CreateLiveLockService(scope);
        var riderId = $"rider_race_{Guid.NewGuid():N}";
        const int concurrency = 100;

        var acquireResults = new ConcurrentBag<(int Index, string OfferId, bool Acquired)>();

        // 100 tasks race to acquire lock for same riderId simultaneously
        var tasks = Enumerable.Range(0, concurrency).Select(async i =>
        {
            var offerId = $"offer_{i}_{Guid.NewGuid():N}";
            var acquired = await lockService.TryAcquireRiderLockAsync(riderId, offerId, TimeSpan.FromSeconds(30));
            acquireResults.Add((i, offerId, acquired));
        });

        await Task.WhenAll(tasks);

        var winners = acquireResults.Where(r => r.Acquired).ToList();
        var losers = acquireResults.Where(r => !r.Acquired).ToList();

        // Exact-One-Winner Invariant
        Assert.Single(winners);
        Assert.Equal(concurrency - 1, losers.Count);

        // Verify holder on live Redis
        var holder = await lockService.GetLockHolderAsync(riderId);
        Assert.Equal(winners[0].OfferId, holder);

        var isLocked = await lockService.IsLockedAsync(riderId);
        Assert.True(isLocked);

        // Cleanup
        await lockService.ReleaseLockAsync(riderId, winners[0].OfferId);
    }

    [Fact]
    public async Task SubStep_3_1_A2_LiveRedis_OwnershipProtection_OnlyHolderCanRelease()
    {
        using var scope = _factory.Services.CreateScope();
        var (lockService, _, _) = CreateLiveLockService(scope);
        var riderId = $"rider_own_{Guid.NewGuid():N}";
        var winnerOfferId = $"offer_winner_{Guid.NewGuid():N}";
        var imposterOfferId = $"offer_imposter_{Guid.NewGuid():N}";

        // Winner acquires
        var acquired = await lockService.TryAcquireRiderLockAsync(riderId, winnerOfferId, TimeSpan.FromSeconds(30));
        Assert.True(acquired);

        // Imposter attempts to release
        var imposterReleased = await lockService.ReleaseLockAsync(riderId, imposterOfferId);
        Assert.False(imposterReleased, "Imposter must not be able to release another worker's lock.");

        // Lock must still be held by winner
        var currentHolder = await lockService.GetLockHolderAsync(riderId);
        Assert.Equal(winnerOfferId, currentHolder);
        Assert.True(await lockService.IsLockedAsync(riderId));

        // Winner releases
        var winnerReleased = await lockService.ReleaseLockAsync(riderId, winnerOfferId);
        Assert.True(winnerReleased, "Winner must be able to release its own lock.");

        // Lock is now free
        Assert.Null(await lockService.GetLockHolderAsync(riderId));
        Assert.False(await lockService.IsLockedAsync(riderId));
    }

    [Fact]
    public async Task SubStep_3_1_A3_LiveRedis_TtlExpirationTakeover()
    {
        using var scope = _factory.Services.CreateScope();
        var (lockService, _, _) = CreateLiveLockService(scope);
        var riderId = $"rider_ttl_{Guid.NewGuid():N}";
        var firstOfferId = $"offer_first_{Guid.NewGuid():N}";
        var secondOfferId = $"offer_second_{Guid.NewGuid():N}";

        // Acquire with short TTL: 300ms
        var firstAcquired = await lockService.TryAcquireRiderLockAsync(riderId, firstOfferId, TimeSpan.FromMilliseconds(300));
        Assert.True(firstAcquired);

        // Immediate competing acquire fails
        var immediateAcquired = await lockService.TryAcquireRiderLockAsync(riderId, secondOfferId, TimeSpan.FromSeconds(10));
        Assert.False(immediateAcquired);

        // Wait for TTL to expire
        await Task.Delay(450);

        // Second offer takes over
        var takeoverAcquired = await lockService.TryAcquireRiderLockAsync(riderId, secondOfferId, TimeSpan.FromSeconds(10));
        Assert.True(takeoverAcquired, "Expired Redis lock must be claimable by next offer.");
        Assert.Equal(secondOfferId, await lockService.GetLockHolderAsync(riderId));

        // Cleanup
        await lockService.ReleaseLockAsync(riderId, secondOfferId);
    }

    // ─────────────────────────────────────────────────────────────────
    // Sub-step 3.1-B: PostgreSQL Fallback Under Redis Outage
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SubStep_3_1_B1_PostgresFallback_100ConcurrencyRace_ExactOneWinner()
    {
        using var scope = _factory.Services.CreateScope();
        var (lockService, db) = CreateFailingRedisLockService(scope);
        var riderId = $"rider_pg_race_{Guid.NewGuid():N}";
        var lockKey = $"dispatch:lock:rider:{riderId}";
        const int concurrency = 100;

        var acquireResults = new ConcurrentBag<(int Index, string OfferId, bool Acquired)>();

        // 100 concurrent tasks race on PostgreSQL fallback table
        var tasks = Enumerable.Range(0, concurrency).Select(async i =>
        {
            var offerId = $"offer_pg_{i}_{Guid.NewGuid():N}";
            var acquired = await lockService.TryAcquireRiderLockAsync(riderId, offerId, TimeSpan.FromSeconds(30));
            acquireResults.Add((i, offerId, acquired));
        });

        await Task.WhenAll(tasks);

        var winners = acquireResults.Where(r => r.Acquired).ToList();
        var losers = acquireResults.Where(r => !r.Acquired).ToList();

        // Exact-One-Winner Invariant on PostgreSQL Fallback
        Assert.Single(winners);
        Assert.Equal(concurrency - 1, losers.Count);

        // Verify in PostgreSQL DistributedLocks table directly
        var lockRow = await db.DistributedLocks.AsNoTracking().FirstOrDefaultAsync(dl => dl.LockKey == lockKey);
        Assert.NotNull(lockRow);
        Assert.Equal(winners[0].OfferId, lockRow.Value);

        // Verify through lockService API
        Assert.Equal(winners[0].OfferId, await lockService.GetLockHolderAsync(riderId));
        Assert.True(await lockService.IsLockedAsync(riderId));

        // Cleanup
        await lockService.ReleaseLockAsync(riderId, winners[0].OfferId);
    }

    [Fact]
    public async Task SubStep_3_1_B2_PostgresFallback_OwnershipProtection_OnlyHolderCanRelease()
    {
        using var scope = _factory.Services.CreateScope();
        var (lockService, db) = CreateFailingRedisLockService(scope);
        var riderId = $"rider_pg_own_{Guid.NewGuid():N}";
        var lockKey = $"dispatch:lock:rider:{riderId}";
        var winnerOfferId = $"offer_winner_pg_{Guid.NewGuid():N}";
        var imposterOfferId = $"offer_imposter_pg_{Guid.NewGuid():N}";

        // Winner acquires on PG fallback
        var acquired = await lockService.TryAcquireRiderLockAsync(riderId, winnerOfferId, TimeSpan.FromSeconds(30));
        Assert.True(acquired);

        // Imposter fails to release
        var imposterReleased = await lockService.ReleaseLockAsync(riderId, imposterOfferId);
        Assert.False(imposterReleased);

        // Row remains in PG
        var row = await db.DistributedLocks.AsNoTracking().FirstOrDefaultAsync(dl => dl.LockKey == lockKey);
        Assert.NotNull(row);
        Assert.Equal(winnerOfferId, row.Value);

        // Winner releases
        var winnerReleased = await lockService.ReleaseLockAsync(riderId, winnerOfferId);
        Assert.True(winnerReleased);

        // Row deleted from PG
        var deletedRow = await db.DistributedLocks.AsNoTracking().FirstOrDefaultAsync(dl => dl.LockKey == lockKey);
        Assert.Null(deletedRow);
    }

    [Fact]
    public async Task SubStep_3_1_B3_PostgresFallback_TtlExpirationTakeover()
    {
        using var scope = _factory.Services.CreateScope();
        var (lockService, db) = CreateFailingRedisLockService(scope);
        var riderId = $"rider_pg_ttl_{Guid.NewGuid():N}";
        var lockKey = $"dispatch:lock:rider:{riderId}";
        var firstOfferId = $"offer_pg_first_{Guid.NewGuid():N}";
        var secondOfferId = $"offer_pg_second_{Guid.NewGuid():N}";

        // 1. First offer acquires with 200ms timeout
        var firstAcquired = await lockService.TryAcquireRiderLockAsync(riderId, firstOfferId, TimeSpan.FromMilliseconds(200));
        Assert.True(firstAcquired);

        // 2. Competing acquire fails while active
        var compAcquired = await lockService.TryAcquireRiderLockAsync(riderId, secondOfferId, TimeSpan.FromSeconds(10));
        Assert.False(compAcquired);

        // 3. Wait 350ms for PostgreSQL lock to expire
        await Task.Delay(350);

        // 4. Second offer takes over expired lock atomically via ON CONFLICT DO UPDATE
        var takeoverAcquired = await lockService.TryAcquireRiderLockAsync(riderId, secondOfferId, TimeSpan.FromSeconds(10));
        Assert.True(takeoverAcquired, "Expired PostgreSQL fallback lock must be taken over atomically.");

        // Check PG table row is updated to secondOfferId
        var row = await db.DistributedLocks.AsNoTracking().FirstOrDefaultAsync(dl => dl.LockKey == lockKey);
        Assert.NotNull(row);
        Assert.Equal(secondOfferId, row.Value);

        // Cleanup
        await lockService.ReleaseLockAsync(riderId, secondOfferId);
    }

    // ─────────────────────────────────────────────────────────────────
    // Sub-step 3.1-C: Failover & Recovery Dynamics
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SubStep_3_1_C_FailoverAndRecovery_CleanLifecycle()
    {
        using var scope = _factory.Services.CreateScope();
        var riderId = $"rider_failover_{Guid.NewGuid():N}";
        var offerId = $"offer_failover_{Guid.NewGuid():N}";

        // Phase 1: Outage occurs -> Worker acquires lock on PostgreSQL fallback
        var (failingService, db) = CreateFailingRedisLockService(scope);
        var pgAcquired = await failingService.TryAcquireRiderLockAsync(riderId, offerId, TimeSpan.FromSeconds(30));
        Assert.True(pgAcquired);
        Assert.Equal(offerId, await failingService.GetLockHolderAsync(riderId));

        // Phase 2: Outage persists -> Worker safely releases lock on PostgreSQL fallback
        var pgReleased = await failingService.ReleaseLockAsync(riderId, offerId);
        Assert.True(pgReleased);
        Assert.Null(await failingService.GetLockHolderAsync(riderId));

        // Phase 3: Redis recovers -> Normal live service acquires lock on Redis cleanly
        var (liveService, _, _) = CreateLiveLockService(scope);
        var newOfferId = $"offer_recovered_{Guid.NewGuid():N}";
        var liveAcquired = await liveService.TryAcquireRiderLockAsync(riderId, newOfferId, TimeSpan.FromSeconds(30));
        Assert.True(liveAcquired);
        Assert.Equal(newOfferId, await liveService.GetLockHolderAsync(riderId));

        // Live release
        var liveReleased = await liveService.ReleaseLockAsync(riderId, newOfferId);
        Assert.True(liveReleased);
    }
}

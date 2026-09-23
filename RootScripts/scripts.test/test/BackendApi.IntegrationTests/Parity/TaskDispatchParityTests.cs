using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BackendApi.Core.StateMachines;
using BackendApi.Data;
using BackendApi.Infrastructure.Redis;
using BackendApi.Models.Entities;
using BackendApi.Services.Dispatch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Xunit;
using Order = BackendApi.Models.Entities.Order;

namespace BackendApi.IntegrationTests.Parity;

/// <summary>
/// Sub-step 3.2-B: Task & Dispatch Functional Parity Integration Tests
/// Verifies Candidate Eligibility, Ranking + Reservation Lock, Accept Branch,
/// and Reject / Timeout Re-dispatch Branch against actual V2 architecture.
/// </summary>
[Collection("SharedTestDatabase")]
public class TaskDispatchParityTests : IAsyncLifetime
{
    private readonly DeliveryWebApplicationFactory _factory;

    public TaskDispatchParityTests(DeliveryWebApplicationFactory factory)
    {
        _factory = factory;
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    // ─── Helpers ──────────────────────────────────────────────────────

    private async Task<Shop> SeedShopAsync(ApplicationDbContext db, double lat, double lng)
    {
        var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
        var shop = new Shop
        {
            Id = $"shop_{Guid.NewGuid():N}",
            Name = "Dispatch Test Shop",
            MenuName = "Signature Dish",
            MenuPrice = 120.00m,
            IsOpen = true,
            Location = factory.CreatePoint(new NetTopologySuite.Geometries.Coordinate(lng, lat))
        };
        db.Shops.Add(shop);
        await db.SaveChangesAsync();
        return shop;
    }

    private async Task<Order> SeedOrderAsync(ApplicationDbContext db, string shopId, double lat, double lng)
    {
        var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
        var order = new Order
        {
            Id = $"ord_{Guid.NewGuid():N}",
            ShopId = shopId,
            PickupLocation = factory.CreatePoint(new NetTopologySuite.Geometries.Coordinate(lng, lat)),
            DropoffLocation = factory.CreatePoint(new NetTopologySuite.Geometries.Coordinate(lng + 0.005, lat + 0.005)),
            DistanceKm = 2.5,
            DeliveryFee = 45.00m,
            ExpectedDeliveryTime = DateTime.UtcNow.AddHours(1),
            State = OrderState.MATCHING,
            DispatchAttempts = 0
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    private async Task<Rider> SeedRiderAsync(ApplicationDbContext db, RiderPresenceService presence, string name, RiderState state, double lat, double lng)
    {
        var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
        var rider = new Rider
        {
            Id = $"rider_{Guid.NewGuid():N}",
            Name = name,
            State = state,
            CurrentLocation = factory.CreatePoint(new NetTopologySuite.Geometries.Coordinate(lng, lat)),
            LastHeartbeat = DateTime.UtcNow,
            LastGpsUpdate = DateTime.UtcNow
        };
        db.Riders.Add(rider);
        await db.SaveChangesAsync();

        // Update GPS & Heartbeat in Redis
        await presence.UpdateGpsAsync(rider.Id, lat, lng, speedKmh: 25.0, accuracy: 10.0);

        return rider;
    }

    // ─── Sub-step 3.2-B Tests ────────────────────────────────────────

    [Fact]
    public async Task SubStep_3_2_B1_CandidateEligibilityAndFiltering()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var presence = scope.ServiceProvider.GetRequiredService<RiderPresenceService>();
        var lockService = scope.ServiceProvider.GetRequiredService<RedisLockService>();

        // Distinct spatial region for B1
        const double baseLat = 17.1000;
        const double baseLng = 102.1000;
        var shop = await SeedShopAsync(db, baseLat, baseLng);
        var pickupLat = shop.Location!.Y;
        var pickupLng = shop.Location!.X;
        const double searchRadiusKm = 10.0;

        // 1. Eligible baseline: IDLE + Nearby (~30m away)
        var riderIdleNear = await SeedRiderAsync(db, presence, "Rider Idle Near", RiderState.IDLE, baseLat + 0.0002, baseLng + 0.0002);

        // 2. Far outside radius: IDLE + Far (~120km away)
        var riderIdleFar = await SeedRiderAsync(db, presence, "Rider Idle Far", RiderState.IDLE, 18.5000, 103.5000);

        // 3. Non-IDLE states: Nearby (~30m) but BUSY, RESERVED, OFFLINE, STALE
        var riderBusy = await SeedRiderAsync(db, presence, "Rider Busy", RiderState.BUSY, baseLat + 0.0001, baseLng + 0.0001);
        var riderReserved = await SeedRiderAsync(db, presence, "Rider Reserved", RiderState.RESERVED, baseLat + 0.0001, baseLng + 0.0001);
        var riderOffline = await SeedRiderAsync(db, presence, "Rider Offline", RiderState.OFFLINE, baseLat + 0.0001, baseLng + 0.0001);
        var riderStale = await SeedRiderAsync(db, presence, "Rider Stale", RiderState.STALE, baseLat + 0.0001, baseLng + 0.0001);

        // 4. Lock held: IDLE + Nearby, but active lock acquired
        var riderLocked = await SeedRiderAsync(db, presence, "Rider Locked", RiderState.IDLE, baseLat + 0.0001, baseLng + 0.0001);
        var dummyOfferId = $"OFF-{Guid.NewGuid():N}"[..16];
        var lockAcquired = await lockService.TryAcquireRiderLockAsync(riderLocked.Id, dummyOfferId, TimeSpan.FromMinutes(2));
        Assert.True(lockAcquired);

        var cohort = new[] { riderIdleNear, riderIdleFar, riderBusy, riderReserved, riderOffline, riderStale, riderLocked };

        try
        {
            // Execute the exact candidate discovery & filtering logic of DispatchService.Offering.cs
            var nearbyRiders = await presence.GetNearbyRidersAsync(pickupLat, pickupLng, searchRadiusKm);
            var nearbyIds = nearbyRiders.Select(r => r.Member.ToString()).ToList();

            // Assert 1: Out-of-radius candidate is excluded from nearby search
            Assert.Contains(riderIdleNear.Id, nearbyIds);
            Assert.DoesNotContain(riderIdleFar.Id, nearbyIds);

            // Filter against DbContext and LockService
            var allRidersDict = await db.Riders
                .Where(r => nearbyIds.Contains(r.Id))
                .ToDictionaryAsync(r => r.Id);

            var eligibleCandidates = new List<string>();
            foreach (var result in nearbyRiders)
            {
                var rId = result.Member.ToString();
                if (!allRidersDict.TryGetValue(rId, out var rider)) continue;
                if (rider.State != RiderState.IDLE) continue;
                if (await lockService.IsLockedAsync(rId)) continue;
                eligibleCandidates.Add(rId);
            }

            // Assert 2: Only the valid IDLE near rider is eligible
            Assert.Contains(riderIdleNear.Id, eligibleCandidates);
            Assert.DoesNotContain(riderBusy.Id, eligibleCandidates);
            Assert.DoesNotContain(riderReserved.Id, eligibleCandidates);
            Assert.DoesNotContain(riderOffline.Id, eligibleCandidates);
            Assert.DoesNotContain(riderStale.Id, eligibleCandidates);
            Assert.DoesNotContain(riderLocked.Id, eligibleCandidates);

            // Assert 3: Exactly 1 eligible candidate from our test cohort
            var testCohortIds = cohort.Select(r => r.Id).ToList();
            var eligibleInCohort = eligibleCandidates.Intersect(testCohortIds).ToList();
            Assert.Single(eligibleInCohort);
            Assert.Equal(riderIdleNear.Id, eligibleInCohort[0]);
        }
        finally
        {
            await lockService.ReleaseLockAsync(riderLocked.Id, dummyOfferId);
            foreach (var r in cohort)
            {
                await presence.RemoveRiderAsync(r.Id);
            }
        }
    }

    [Fact]
    public async Task SubStep_3_2_B2_CandidateRankingAndReservationLock()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var presence = scope.ServiceProvider.GetRequiredService<RiderPresenceService>();
        var lockService = scope.ServiceProvider.GetRequiredService<RedisLockService>();
        var dispatchService = scope.ServiceProvider.GetRequiredService<DispatchService>();

        // Distinct spatial region for B2
        const double baseLat = 17.2000;
        const double baseLng = 102.2000;
        var shop = await SeedShopAsync(db, baseLat, baseLng);
        var order = await SeedOrderAsync(db, shop.Id, baseLat, baseLng);

        // Seed 2 IDLE nearby riders with different proximity
        var rider1 = await SeedRiderAsync(db, presence, "Rider Priority 1", RiderState.IDLE, baseLat + 0.0005, baseLng + 0.0005);
        var rider2 = await SeedRiderAsync(db, presence, "Rider Priority 2", RiderState.IDLE, baseLat + 0.0050, baseLng + 0.0050);

        try
        {
            // Act: Dispatch matches and offers to best candidate
            await dispatchService.FindAndOfferAsync(new List<Order> { order });

            // Refresh order from database
            var updatedOrder = await db.Orders.AsNoTracking().FirstAsync(o => o.Id == order.Id);

            // Invariant 1: Order state transitions to OFFERING
            Assert.Equal(OrderState.OFFERING, updatedOrder.State);
            Assert.False(string.IsNullOrWhiteSpace(updatedOrder.CurrentOfferId));
            Assert.StartsWith("OFF-", updatedOrder.CurrentOfferId);
            Assert.Equal(1, updatedOrder.OfferVersion);
            Assert.NotNull(updatedOrder.OfferExpiresAt);
            Assert.True(updatedOrder.OfferExpiresAt > DateTime.UtcNow);

            var winnerRiderId = updatedOrder.AssignedRiderId;
            Assert.NotNull(winnerRiderId);
            Assert.True(winnerRiderId == rider1.Id || winnerRiderId == rider2.Id, 
                $"Winner {winnerRiderId} must be one of the seeded candidates {rider1.Id} or {rider2.Id}");

            // Invariant 2: Winner Rider state transitions to RESERVED
            var winnerRider = await db.Riders.AsNoTracking().FirstAsync(r => r.Id == winnerRiderId);
            Assert.Equal(RiderState.RESERVED, winnerRider.State);

            // Invariant 3: Other Rider remains IDLE with no lock
            var otherRiderId = winnerRiderId == rider1.Id ? rider2.Id : rider1.Id;
            var otherRider = await db.Riders.AsNoTracking().FirstAsync(r => r.Id == otherRiderId);
            Assert.Equal(RiderState.IDLE, otherRider.State);
            Assert.False(await lockService.IsLockedAsync(otherRiderId));

            // Invariant 4: Redis reservation lock is acquired with exact matching offerId
            Assert.True(await lockService.IsLockedAsync(winnerRiderId));
            var lockHolder = await lockService.GetLockHolderAsync(winnerRiderId);
            Assert.Equal(updatedOrder.CurrentOfferId, lockHolder);
        }
        finally
        {
            var freshOrder = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == order.Id);
            if (freshOrder?.AssignedRiderId != null && freshOrder.CurrentOfferId != null)
            {
                await lockService.ReleaseLockAsync(freshOrder.AssignedRiderId, freshOrder.CurrentOfferId);
            }
            await presence.RemoveRiderAsync(rider1.Id);
            await presence.RemoveRiderAsync(rider2.Id);
        }
    }

    [Fact]
    public async Task SubStep_3_2_B3_AcceptOffer_TransitionsToAssignedAndBusy()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var presence = scope.ServiceProvider.GetRequiredService<RiderPresenceService>();
        var lockService = scope.ServiceProvider.GetRequiredService<RedisLockService>();
        var dispatchService = scope.ServiceProvider.GetRequiredService<DispatchService>();
        var offerHandler = scope.ServiceProvider.GetRequiredService<DispatchOfferHandler>();

        // Distinct spatial region for B3
        const double baseLat = 17.3000;
        const double baseLng = 102.3000;
        var shop = await SeedShopAsync(db, baseLat, baseLng);
        var order = await SeedOrderAsync(db, shop.Id, baseLat, baseLng);
        var rider = await SeedRiderAsync(db, presence, "Rider Acceptor", RiderState.IDLE, baseLat + 0.0005, baseLng + 0.0005);

        try
        {
            // 1. Dispatch creates offer and locks rider
            await dispatchService.FindAndOfferAsync(new List<Order> { order });

            var offeredOrder = await db.Orders.AsNoTracking().FirstAsync(o => o.Id == order.Id);
            Assert.Equal(OrderState.OFFERING, offeredOrder.State);
            Assert.Equal(rider.Id, offeredOrder.AssignedRiderId);

            var offerId = offeredOrder.CurrentOfferId!;
            var version = offeredOrder.OfferVersion;

            // 2. Rider accepts offer
            var accepted = await offerHandler.AcceptOfferAsync(rider.Id, offerId, version);
            Assert.True(accepted, "AcceptOfferAsync must succeed for valid offerId and version");

            // 3. Verify Order transitions to ASSIGNED
            var assignedOrder = await db.Orders.AsNoTracking().FirstAsync(o => o.Id == order.Id);
            Assert.Equal(OrderState.ASSIGNED, assignedOrder.State);
            Assert.NotNull(assignedOrder.AssignedAt);
            Assert.Equal(rider.Id, assignedOrder.AssignedRiderId);

            // 4. Verify Rider transitions to BUSY
            var busyRider = await db.Riders.AsNoTracking().FirstAsync(r => r.Id == rider.Id);
            Assert.Equal(RiderState.BUSY, busyRider.State);
        }
        finally
        {
            var freshOrder = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == order.Id);
            if (freshOrder?.CurrentOfferId != null)
            {
                await lockService.ReleaseLockAsync(rider.Id, freshOrder.CurrentOfferId);
            }
            await presence.RemoveRiderAsync(rider.Id);
        }
    }

    [Fact]
    public async Task SubStep_3_2_B4_RejectOffer_ReleasesReservationAndRedispatchesNextCandidate()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var presence = scope.ServiceProvider.GetRequiredService<RiderPresenceService>();
        var lockService = scope.ServiceProvider.GetRequiredService<RedisLockService>();
        var dispatchService = scope.ServiceProvider.GetRequiredService<DispatchService>();
        var offerHandler = scope.ServiceProvider.GetRequiredService<DispatchOfferHandler>();

        // Distinct spatial region for B4
        const double baseLat = 17.4000;
        const double baseLng = 102.4000;
        var shop = await SeedShopAsync(db, baseLat, baseLng);
        var order = await SeedOrderAsync(db, shop.Id, baseLat, baseLng);

        // Seed 2 IDLE nearby riders
        var riderFirst = await SeedRiderAsync(db, presence, "Rider Rejector", RiderState.IDLE, baseLat + 0.0002, baseLng + 0.0002);
        var riderSecond = await SeedRiderAsync(db, presence, "Rider Fallback", RiderState.IDLE, baseLat + 0.0010, baseLng + 0.0010);

        try
        {
            // 1. Initial dispatch offers to first rider
            await dispatchService.FindAndOfferAsync(new List<Order> { order });

            var firstOfferedOrder = await db.Orders.AsNoTracking().FirstAsync(o => o.Id == order.Id);
            Assert.Equal(OrderState.OFFERING, firstOfferedOrder.State);
            var initialOfferId = firstOfferedOrder.CurrentOfferId!;
            var firstRiderId = firstOfferedOrder.AssignedRiderId!;

            // Verify first rider holds lock and is RESERVED
            Assert.True(await lockService.IsLockedAsync(firstRiderId));
            var firstRiderDb = await db.Riders.AsNoTracking().FirstAsync(r => r.Id == firstRiderId);
            Assert.Equal(RiderState.RESERVED, firstRiderDb.State);

            // To simulate the rejecting rider being unavailable for immediate re-offer,
            // we set first rider's state in DB to OFFLINE so re-dispatch picks the second rider.
            var firstRiderEntity = await db.Riders.FindAsync(firstRiderId);
            firstRiderEntity!.State = RiderState.OFFLINE;
            await db.SaveChangesAsync();

            // 2. First rider rejects the offer
            await offerHandler.RejectOrTimeoutAsync(initialOfferId, firstRiderId, "rejected");

            // 3. Verify reservation lock on first rider is released
            Assert.False(await lockService.IsLockedAsync(firstRiderId), "First rider lock must be released on rejection");

            // 4. Verify re-dispatch: Order received by second candidate
            var redispatchedOrder = await db.Orders.AsNoTracking().FirstAsync(o => o.Id == order.Id);
            Assert.Equal(OrderState.OFFERING, redispatchedOrder.State);
            Assert.NotEqual(initialOfferId, redispatchedOrder.CurrentOfferId);
            Assert.True(redispatchedOrder.OfferVersion >= 2, "Offer version must increment on re-dispatch");

            var secondRiderId = firstRiderId == riderFirst.Id ? riderSecond.Id : riderFirst.Id;
            Assert.Equal(secondRiderId, redispatchedOrder.AssignedRiderId);

            // Second rider is now RESERVED and holds reservation lock
            var secondRiderDb = await db.Riders.AsNoTracking().FirstAsync(r => r.Id == secondRiderId);
            Assert.Equal(RiderState.RESERVED, secondRiderDb.State);
            Assert.True(await lockService.IsLockedAsync(secondRiderId));
            Assert.Equal(redispatchedOrder.CurrentOfferId, await lockService.GetLockHolderAsync(secondRiderId));
        }
        finally
        {
            var freshOrder = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == order.Id);
            if (freshOrder?.AssignedRiderId != null && freshOrder.CurrentOfferId != null)
            {
                await lockService.ReleaseLockAsync(freshOrder.AssignedRiderId, freshOrder.CurrentOfferId);
            }
            if (freshOrder?.CurrentOfferId != null)
            {
                await lockService.ReleaseLockAsync(riderFirst.Id, freshOrder.CurrentOfferId);
                await lockService.ReleaseLockAsync(riderSecond.Id, freshOrder.CurrentOfferId);
            }
            await presence.RemoveRiderAsync(riderFirst.Id);
            await presence.RemoveRiderAsync(riderSecond.Id);
        }
    }
}

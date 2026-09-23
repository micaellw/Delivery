using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using BackendApi.Core.Models.Response;
using BackendApi.Core.StateMachines;
using BackendApi.Data;
using BackendApi.Services.Dispatch;
using BackendApi.Infrastructure.Redis;
using BackendApi.Models.DTOs;
using BackendApi.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using StackExchange.Redis;
using Xunit;
using Order = BackendApi.Models.Entities.Order;

namespace BackendApi.IntegrationTests.Parity;

/// <summary>
/// Sub-step 3.2-C: Rider Journey Functional Parity Integration Tests
/// Verifies:
/// 1. Rider authentication via Role + NameIdentifier/UserId claim and backend resolution to User.RiderId.
/// 2. Offer acceptance transitioning Order -> ASSIGNED and Rider -> BUSY.
/// 3. In-flight GPS telemetry accuracy contract (<=50m Core, >50m && <=300m Degraded, >300m Rejected) and Redis presence updates.
/// 4. Full progression ASSIGNED -> PICKING_UP -> DELIVERING -> COMPLETED, CompletedAt timestamp, and automatic return to IDLE.
/// 5. Security guards: Imposter rider rejection (403 Forbidden) and illegal state transition prevention (400 Bad Request).
/// </summary>
[Collection("SharedTestDatabase")]
public class RiderJourneyParityTests : IAsyncLifetime
{
    private readonly DeliveryWebApplicationFactory _factory;
    private HttpClient _client = default!;
    private readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };

    public RiderJourneyParityTests(DeliveryWebApplicationFactory factory)
    {
        _factory = factory;
    }

    public Task InitializeAsync()
    {
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ─── Helper Records & DTOs ────────────────────────────────────────────────────────

    private record RegisterPayload(string Email, string Password, string FullName, string Role);
    private record ApiResponseWrapper<T>(bool Success, T? Value, string? Message, List<string>? Errors);
    private record AuthData(string AccessToken, string RefreshToken, DateTime ExpiresAt, UserInfo? User);
    private record UserInfo(string Id, string Email, string Role, string? FullName);

    // ─── Helpers ──────────────────────────────────────────────────────────────────────

    private async Task<(string AccessToken, string UserId, string RiderId)> RegisterAndLoginRiderAsync(string prefix = "rider")
    {
        var email = $"{prefix}_{Guid.NewGuid():N}@test.com";
        var payload = new RegisterPayload(email, "P@ssword123!", "Parity Rider", "Rider");
        var resp = await _client.PostAsJsonAsync("/api/v1/auth/register", payload);
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<ApiResponseWrapper<AuthData>>(_jsonOpts);
        var token = body!.Value!.AccessToken;
        var userId = body.Value.User!.Id;

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        Assert.NotNull(user);
        Assert.NotNull(user.RiderId);

        return (token, userId, user.RiderId);
    }

    private static HttpRequestMessage CreateAuthRequest(HttpMethod method, string uri, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        return request;
    }

    private static GeometryFactory GeoFactory =>
        NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);

    // ─── Test C1: Authentication & Identity Resolution ────────────────────────────────

    [Fact]
    public async Task SubStep_3_2_C1_RiderAuthAndIdentityResolution_ResolvesRiderProfile()
    {
        // 1. Register & Login as Rider
        var (token, userId, riderId) = await RegisterAndLoginRiderAsync("c1_auth");

        // 2. Inspect JWT Claims: Role == Rider, NameIdentifier == userId, RiderId claim MUST NOT exist
        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);

        var roleClaim = jwt.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Role || c.Type == "role")?.Value;
        var nameIdClaim = jwt.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier || c.Type == "nameid" || c.Type == "sub")?.Value;
        var riderIdClaim = jwt.Claims.FirstOrDefault(c => c.Type.Equals("RiderId", StringComparison.OrdinalIgnoreCase) || c.Type.Equals("rider_id", StringComparison.OrdinalIgnoreCase))?.Value;

        Assert.Equal("Rider", roleClaim);
        Assert.Equal(userId, nameIdClaim);
        Assert.Null(riderIdClaim); // Invariant: JWT does NOT expose RiderId directly

        // 3. Verify PostgreSQL persistence: User links to Rider with initial OFFLINE state
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var rider = await db.Riders.AsNoTracking().FirstOrDefaultAsync(r => r.Id == riderId);
            Assert.NotNull(rider);
            Assert.Equal(RiderState.OFFLINE, rider.State);
        }

        // 4. Call GET /api/v1/orders/my: Backend resolves User.RiderId from token userId
        var req = CreateAuthRequest(HttpMethod.Get, "/api/v1/orders/my", token);
        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var myOrders = await resp.Content.ReadFromJsonAsync<ApiResponseWrapper<List<OrderDto>>>(_jsonOpts);
        Assert.NotNull(myOrders);
        Assert.True(myOrders.Success);
        Assert.NotNull(myOrders.Value);
    }

    // ─── Test C2: Offer Acceptance Branch ─────────────────────────────────────────────

    [Fact]
    public async Task SubStep_3_2_C2_OfferAcceptance_TransitionsToAssignedAndBusy()
    {
        var (_, _, riderId) = await RegisterAndLoginRiderAsync("c2_accept");
        var offerId = $"off_c2_{Guid.NewGuid():N}";

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var redis = scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>();
        var redisDb = redis.GetDatabase();

        // 1. Seed Shop & Order in OFFERING state
        var shop = new Shop
        {
            Id = $"shop_c2_{Guid.NewGuid():N}",
            Name = "C2 Shop",
            IsOpen = true,
            Location = GeoFactory.CreatePoint(new Coordinate(100.51, 13.76))
        };
        db.Shops.Add(shop);

        var order = new Order
        {
            Id = $"ord_c2_{Guid.NewGuid():N}",
            ShopId = shop.Id,
            PickupLocation = shop.Location,
            DropoffLocation = GeoFactory.CreatePoint(new Coordinate(100.52, 13.77)),
            DistanceKm = 2.0,
            DeliveryFee = 40.00m,
            ExpectedDeliveryTime = DateTime.UtcNow.AddHours(1),
            State = OrderState.OFFERING,
            CurrentOfferId = offerId,
            OfferVersion = 1,
            OfferExpiresAt = DateTime.UtcNow.AddSeconds(30),
            AssignedRiderId = riderId
        };
        db.Orders.Add(order);

        // 2. Set Rider to RESERVED state and acquire reservation lock
        var rider = await db.Riders.FindAsync(riderId);
        Assert.NotNull(rider);
        rider.State = RiderState.RESERVED;
        await db.SaveChangesAsync();

        var riderLockKey = $"dispatch:lock:rider:{riderId}";
        await redisDb.StringSetAsync(riderLockKey, offerId, TimeSpan.FromSeconds(30));

        try
        {
            // 3. Accept Offer via DispatchOfferHandler
            var offerHandler = scope.ServiceProvider.GetRequiredService<DispatchOfferHandler>();
            var accepted = await offerHandler.AcceptOfferAsync(riderId, offerId, 1);
            Assert.True(accepted, "Offer acceptance should succeed for valid offer and version.");

            // 4. Verify Order transitions to ASSIGNED with AssignedAt
            var reloadedOrder = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == order.Id);
            Assert.NotNull(reloadedOrder);
            Assert.Equal(OrderState.ASSIGNED, reloadedOrder.State);
            Assert.Equal(riderId, reloadedOrder.AssignedRiderId);
            Assert.NotNull(reloadedOrder.AssignedAt);

            // 5. Verify Rider transitions to BUSY
            var reloadedRider = await db.Riders.AsNoTracking().FirstOrDefaultAsync(r => r.Id == riderId);
            Assert.NotNull(reloadedRider);
            Assert.Equal(RiderState.BUSY, reloadedRider.State);

            // 6. Verify offer concurrency lock is released
            var offerLockExists = await redisDb.KeyExistsAsync($"lock:offer:{offerId}");
            Assert.False(offerLockExists, "Offer concurrency lock must be released after acceptance.");
        }
        finally
        {
            await redisDb.KeyDeleteAsync(riderLockKey);
        }
    }

    // ─── Test C3: GPS Telemetry & Accuracy Contract ───────────────────────────────────

    [Fact]
    public async Task SubStep_3_2_C3_GpsTelemetryIngestion_EnforcesAccuracyAndUpdatesRedisPresence()
    {
        var (token, _, riderId) = await RegisterAndLoginRiderAsync("c3_gps");

        using var scope = _factory.Services.CreateScope();
        var presence = scope.ServiceProvider.GetRequiredService<RiderPresenceService>();
        var redis = scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>();
        var db = redis.GetDatabase();

        // Unique coordinates for spatial test
        var coreLat = 13.8201;
        var coreLng = 100.5501;

        try
        {
            // ── Case A: Standard Core Accuracy (15.0m <= 50.0m) ───────────────────────
            var corePayload = new
            {
                Latitude = coreLat,
                Longitude = coreLng,
                Accuracy = 15.0,
                Timestamp = DateTime.UtcNow
            };
            var req1 = CreateAuthRequest(HttpMethod.Post, "/api/v1/telemetry/gps", token, corePayload);
            var resp1 = await _client.SendAsync(req1);
            Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);

            // Verify Redis Presence Cache updated
            var lastLoc = await presence.GetLastKnownLocationAsync(riderId);
            Assert.NotNull(lastLoc);
            Assert.Equal(coreLat, lastLoc.Value.Lat, 4);
            Assert.Equal(coreLng, lastLoc.Value.Lng, 4);

            // Verify Redis Hash contains accuracy 15
            var hashEntries = await db.HashGetAllAsync($"riders:gps:{riderId}");
            Assert.NotEmpty(hashEntries);
            var accEntry = hashEntries.FirstOrDefault(e => e.Name == "accuracy").Value;
            Assert.Equal("15", accEntry.ToString());

            // ── Case B: Degraded Accuracy (120.0m > 50.0m && <= 300.0m) ───────────────
            // Reset rate limiter key for test determinism
            await db.KeyDeleteAsync($"rider_last_gps_limit:rest:{riderId}");

            var degradedLat = 13.8300;
            var degradedLng = 100.5600;
            var degradedPayload = new
            {
                Latitude = degradedLat,
                Longitude = degradedLng,
                Accuracy = 120.0,
                Timestamp = DateTime.UtcNow
            };
            var req2 = CreateAuthRequest(HttpMethod.Post, "/api/v1/telemetry/gps", token, degradedPayload);
            var resp2 = await _client.SendAsync(req2);
            Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);

            // Degraded point must NOT overwrite core Redis location
            var locAfterDegraded = await presence.GetLastKnownLocationAsync(riderId);
            Assert.NotNull(locAfterDegraded);
            Assert.Equal(coreLat, locAfterDegraded.Value.Lat, 4);
            Assert.Equal(coreLng, locAfterDegraded.Value.Lng, 4);

            // ── Case C: Unusable Accuracy (350.0m > 300.0m) ────────────────────────────
            await db.KeyDeleteAsync($"rider_last_gps_limit:rest:{riderId}");

            var unusablePayload = new
            {
                Latitude = 13.8999,
                Longitude = 100.5999,
                Accuracy = 350.0,
                Timestamp = DateTime.UtcNow
            };
            var req3 = CreateAuthRequest(HttpMethod.Post, "/api/v1/telemetry/gps", token, unusablePayload);
            var resp3 = await _client.SendAsync(req3);
            Assert.Equal(HttpStatusCode.OK, resp3.StatusCode);

            // Core Redis location still preserved
            var locAfterUnusable = await presence.GetLastKnownLocationAsync(riderId);
            Assert.NotNull(locAfterUnusable);
            Assert.Equal(coreLat, locAfterUnusable.Value.Lat, 4);
            Assert.Equal(coreLng, locAfterUnusable.Value.Lng, 4);
        }
        finally
        {
            await presence.RemoveRiderAsync(riderId);
            await db.KeyDeleteAsync($"rider_last_gps_limit:rest:{riderId}");
        }
    }

    // ─── Test C4: Full Lifecycle Progression & Automatic Idle Return ──────────────────

    [Fact]
    public async Task SubStep_3_2_C4_FullJourneyProgression_PickingUpToDeliveringToCompleted_RiderReturnsIdle()
    {
        var (token, _, riderId) = await RegisterAndLoginRiderAsync("c4_progression");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var redis = scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>();
        var redisDb = redis.GetDatabase();

        // 1. Seed Shop & Order in ASSIGNED state
        var shop = new Shop
        {
            Id = $"shop_c4_{Guid.NewGuid():N}",
            Name = "C4 Shop",
            IsOpen = true,
            Location = GeoFactory.CreatePoint(new Coordinate(100.53, 13.78))
        };
        db.Shops.Add(shop);

        var order = new Order
        {
            Id = $"ord_c4_{Guid.NewGuid():N}",
            ShopId = shop.Id,
            PickupLocation = shop.Location,
            DropoffLocation = GeoFactory.CreatePoint(new Coordinate(100.54, 13.79)),
            DistanceKm = 3.2,
            DeliveryFee = 50.00m,
            ExpectedDeliveryTime = DateTime.UtcNow.AddHours(1),
            State = OrderState.ASSIGNED,
            AssignedRiderId = riderId,
            AssignedAt = DateTime.UtcNow
        };
        db.Orders.Add(order);

        var rider = await db.Riders.FindAsync(riderId);
        Assert.NotNull(rider);
        rider.State = RiderState.BUSY;
        await db.SaveChangesAsync();

        try
        {
            // ── Step 1: ASSIGNED -> PICKING_UP ────────────────────────────────────────
            var req1 = CreateAuthRequest(HttpMethod.Patch, $"/api/v1/orders/{order.Id}/status", token, new { Status = "PICKING_UP" });
            var resp1 = await _client.SendAsync(req1);
            Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);

            var orderAfterPickup = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == order.Id);
            Assert.NotNull(orderAfterPickup);
            Assert.Equal(OrderState.PICKING_UP, orderAfterPickup.State);

            // ── Step 2: PICKING_UP -> DELIVERING ──────────────────────────────────────
            var req2 = CreateAuthRequest(HttpMethod.Patch, $"/api/v1/orders/{order.Id}/status", token, new { Status = "DELIVERING" });
            var resp2 = await _client.SendAsync(req2);
            Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);

            var orderAfterDelivering = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == order.Id);
            Assert.NotNull(orderAfterDelivering);
            Assert.Equal(OrderState.DELIVERING, orderAfterDelivering.State);

            // ── Step 3: DELIVERING -> COMPLETED ───────────────────────────────────────
            var req3 = CreateAuthRequest(HttpMethod.Patch, $"/api/v1/orders/{order.Id}/status", token, new { Status = "COMPLETED" });
            var resp3 = await _client.SendAsync(req3);
            Assert.Equal(HttpStatusCode.OK, resp3.StatusCode);

            var completedOrder = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == order.Id);
            Assert.NotNull(completedOrder);
            Assert.Equal(OrderState.COMPLETED, completedOrder.State);
            Assert.NotNull(completedOrder.CompletedAt);

            // ── Verify Rider automatically returns to IDLE (no remaining active orders)
            var reloadedRider = await db.Riders.AsNoTracking().FirstOrDefaultAsync(r => r.Id == riderId);
            Assert.NotNull(reloadedRider);
            Assert.Equal(RiderState.IDLE, reloadedRider.State);

            var cachedRiderStatus = await redisDb.StringGetAsync($"riders:status:{riderId}");
            Assert.Equal("IDLE", (string?)cachedRiderStatus);
        }
        finally
        {
            await redisDb.KeyDeleteAsync($"riders:status:{riderId}");
        }
    }

    // ─── Test C5: Security Boundary & Illegal Transition Guards ───────────────────────

    [Fact]
    public async Task SubStep_3_2_C5_SecurityGuard_ImposterRiderCannotMutateAnotherRidersOrder()
    {
        // 1. Create Rider A and Rider B
        var (tokenA, _, riderAId) = await RegisterAndLoginRiderAsync("c5_riderA");
        var (tokenB, _, _) = await RegisterAndLoginRiderAsync("c5_riderB");

        // 2. Create Customer
        var custEmail = $"cust_c5_{Guid.NewGuid():N}@test.com";
        var custPayload = new RegisterPayload(custEmail, "P@ssword123!", "Customer C5", "Customer");
        var custRegisterResp = await _client.PostAsJsonAsync("/api/v1/auth/register", custPayload);
        custRegisterResp.EnsureSuccessStatusCode();
        var custAuth = await custRegisterResp.Content.ReadFromJsonAsync<ApiResponseWrapper<AuthData>>(_jsonOpts);
        var tokenCust = custAuth!.Value!.AccessToken;

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // 3. Seed Order assigned to Rider A in ASSIGNED state
        var shop = new Shop
        {
            Id = $"shop_c5_{Guid.NewGuid():N}",
            Name = "C5 Shop",
            IsOpen = true,
            Location = GeoFactory.CreatePoint(new Coordinate(100.55, 13.79))
        };
        db.Shops.Add(shop);

        var order = new Order
        {
            Id = $"ord_c5_{Guid.NewGuid():N}",
            ShopId = shop.Id,
            PickupLocation = shop.Location,
            DropoffLocation = GeoFactory.CreatePoint(new Coordinate(100.56, 13.80)),
            DistanceKm = 1.5,
            DeliveryFee = 35.00m,
            ExpectedDeliveryTime = DateTime.UtcNow.AddHours(1),
            State = OrderState.ASSIGNED,
            AssignedRiderId = riderAId,
            AssignedAt = DateTime.UtcNow
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        // ── Check 1: Imposter Rider B attempts to update Rider A's order ───────────
        var imposterReq = CreateAuthRequest(HttpMethod.Patch, $"/api/v1/orders/{order.Id}/status", tokenB, new { Status = "PICKING_UP" });
        var imposterResp = await _client.SendAsync(imposterReq);
        Assert.Equal(HttpStatusCode.Forbidden, imposterResp.StatusCode);

        var imposterBody = await imposterResp.Content.ReadAsStringAsync();
        Assert.Contains("คุณไม่ได้รับมอบหมายให้ทำออเดอร์นี้", imposterBody);

        // ── Check 2: Customer attempts to update order status ─────────────────────
        var custReq = CreateAuthRequest(HttpMethod.Patch, $"/api/v1/orders/{order.Id}/status", tokenCust, new { Status = "PICKING_UP" });
        var custResp = await _client.SendAsync(custReq);
        Assert.Equal(HttpStatusCode.Forbidden, custResp.StatusCode);

        // ── Check 3: Illegal state transition: Rider A jumps ASSIGNED -> COMPLETED ───
        var illegalReq = CreateAuthRequest(HttpMethod.Patch, $"/api/v1/orders/{order.Id}/status", tokenA, new { Status = "COMPLETED" });
        var illegalResp = await _client.SendAsync(illegalReq);
        Assert.Equal(HttpStatusCode.BadRequest, illegalResp.StatusCode);

        // Verify order state remains strictly ASSIGNED
        var persistedOrder = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == order.Id);
        Assert.NotNull(persistedOrder);
        Assert.Equal(OrderState.ASSIGNED, persistedOrder.State);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using BackendApi.Core.Constants;
using BackendApi.Core.Models.Response;
using BackendApi.Core.StateMachines;
using BackendApi.Data;
using BackendApi.Infrastructure.Redis;
using BackendApi.Models.DTOs;
using BackendApi.Models.Entities;
using BackendApi.Security.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using StackExchange.Redis;
using Xunit;
using Order = BackendApi.Models.Entities.Order;

namespace BackendApi.IntegrationTests.Parity;

/// <summary>
/// Sub-step 3.2-E: Monitoring Functional Parity Integration Tests
/// Verifies:
/// 1. E1: Live Map Initial State & Redis Presence (GET /api/v1/rider-locations)
///    - Redis-first lookup for map pre-loading / F5 refresh without touching PostgreSQL in hot path
///    - Returns complete active rider metrics (RiderId, Lat, Lng, SpeedKmh, Accuracy, Status)
///    - Authorization guard: 200 for Operations, 403 for Customer/Rider.
/// 2. E2: GPS Accuracy Tiering & Redis Separation (POST /api/v1/telemetry/gps)
///    - Core accuracy (<= 50m) updates Redis presence
///    - Degraded accuracy (50m < acc <= 300m) does NOT overwrite Core Redis location
///    - Unusable accuracy (> 300m) is dropped completely
///    - Core coordinates remain preserved after degraded/unusable attempts.
/// 3. E3: Order Route History PostGIS Query & Ordering (GET /api/v1/orders/{id}/route-history)
///    - Queries durable PostgreSQL RiderLocationHistories (NOT Redis stream/cache)
///    - Filters strictly to assigned rider within the job's time window (+/- buffer)
///    - Excludes other riders and out-of-window telemetry points
///    - Enforces strict ascending chronological order (RecordedAt)
///    - Explicitly verifies coordinate mapping: Lat = Point.Y, Lng = Point.X.
/// 4. E4: Route History Security & Edge Cases
///    - Non-existent order returns 404 Not Found (NOT_FOUND)
///    - Unassigned order returns 200 OK with empty ActualGpsPoints list []
///    - Customer returns 403 Forbidden
///    - Rider returns 403 Forbidden
///    - Unauthenticated request returns 401 Unauthorized.
/// </summary>
[Collection("SharedTestDatabase")]
public class MonitoringParityTests : IAsyncLifetime
{
    private readonly DeliveryWebApplicationFactory _factory;
    private HttpClient _client = default!;
    private readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };

    public MonitoringParityTests(DeliveryWebApplicationFactory factory)
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

    private async Task<(string AccessToken, string UserId)> RegisterUserAsync(string role = "Customer")
    {
        if (role is AuthConstants.AdminRole or AuthConstants.DispatcherRole)
            return await _factory.CreatePrivilegedUserAndGetTokenAsync(_client, role);

        var email = $"{role.ToLowerInvariant()}_monitoring_{Guid.NewGuid():N}@test.com";
        var payload = new RegisterPayload(email, "P@ssword123!", $"Monitoring {role}", role);
        var resp = await _client.PostAsJsonAsync("/api/v1/auth/register", payload);
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<ApiResponseWrapper<AuthData>>(_jsonOpts);
        return (body!.Value!.AccessToken, body.Value.User!.Id);
    }

    private static HttpRequestMessage CreateAuthRequest(HttpMethod method, string uri, string? token, object? body = null)
    {
        var request = new HttpRequestMessage(method, uri);
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        return request;
    }

    private static GeometryFactory GeoFactory =>
        NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);

    // ─── Test E1: Live Map Initial State & Redis Presence ─────────────────────────────

    [Fact]
    public async Task SubStep_3_2_E1_LiveMapInitialState_ReadsFromRedisPresence_WithAuthorizationGuards()
    {
        var (adminToken, _) = await RegisterUserAsync(AuthConstants.AdminRole);
        var (custToken, _) = await RegisterUserAsync(AuthConstants.CustomerRole);
        var (riderToken, _) = await RegisterUserAsync(AuthConstants.RiderRole);

        var targetRiderId = $"rider_e1_{Guid.NewGuid():N}";
        const double expectedLat = 13.7563;
        const double expectedLng = 100.5018;
        const double expectedSpeed = 24.5;
        const double expectedAccuracy = 12.0;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var presenceService = scope.ServiceProvider.GetRequiredService<RiderPresenceService>();
            var redis = scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>();
            var redisDb = redis.GetDatabase();

            // 1. Seed Rider entity in DB with IDLE state
            db.Riders.Add(new Rider
            {
                Id = targetRiderId,
                Name = "Live Rider E1",
                State = RiderState.IDLE
            });
            await db.SaveChangesAsync();

            // 2. Seed Redis Presence (GEOADD, Hash riders:gps, and status)
            await presenceService.UpdateGpsAsync(targetRiderId, expectedLat, expectedLng, expectedSpeed, expectedAccuracy);
            await redisDb.StringSetAsync($"riders:status:{targetRiderId}", "IDLE");
        }

        try
        {
            // ── 3. Admin Request (200 OK & Data Consistency with Redis)
            var reqAdmin = CreateAuthRequest(HttpMethod.Get, "/api/v1/rider-locations", adminToken);
            var respAdmin = await _client.SendAsync(reqAdmin);
            Assert.Equal(HttpStatusCode.OK, respAdmin.StatusCode);

            var result = await respAdmin.Content.ReadFromJsonAsync<ApiResponseWrapper<List<RiderLocationDto>>>(_jsonOpts);
            Assert.NotNull(result);
            Assert.True(result.Success);
            Assert.NotNull(result.Value);

            var riderLoc = result.Value.FirstOrDefault(r => r.RiderId == targetRiderId);
            Assert.NotNull(riderLoc);
            Assert.Equal("Live Rider E1", riderLoc.Name);
            Assert.Equal("IDLE", riderLoc.Status);
            Assert.Equal(expectedLat, riderLoc.Lat, 4);
            Assert.Equal(expectedLng, riderLoc.Lng, 4);
            Assert.Equal(expectedSpeed, riderLoc.SpeedKmh, 1);
            Assert.Equal(expectedAccuracy, riderLoc.Accuracy, 1);

            // ── 4. Customer Request (403 Forbidden)
            var reqCust = CreateAuthRequest(HttpMethod.Get, "/api/v1/rider-locations", custToken);
            var respCust = await _client.SendAsync(reqCust);
            Assert.Equal(HttpStatusCode.Forbidden, respCust.StatusCode);

            // ── 5. Rider Request (403 Forbidden)
            var reqRider = CreateAuthRequest(HttpMethod.Get, "/api/v1/rider-locations", riderToken);
            var respRider = await _client.SendAsync(reqRider);
            Assert.Equal(HttpStatusCode.Forbidden, respRider.StatusCode);
        }
        finally
        {
            // Spatial & Redis cache cleanup
            using var cleanupScope = _factory.Services.CreateScope();
            var presenceService = cleanupScope.ServiceProvider.GetRequiredService<RiderPresenceService>();
            var redis = cleanupScope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>();
            await presenceService.RemoveRiderAsync(targetRiderId);
            await redis.GetDatabase().KeyDeleteAsync($"riders:status:{targetRiderId}");
        }
    }

    // ─── Test E2: GPS Accuracy Tiering & Redis Separation ─────────────────────────────

    [Fact]
    public async Task SubStep_3_2_E2_GpsAccuracyTiering_EnforcesCoreUpdateAndPreservesRedisPosition()
    {
        var (riderToken, userId) = await RegisterUserAsync(AuthConstants.RiderRole);

        string riderId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.FirstAsync(u => u.Id == userId);
            riderId = user.RiderId!;
        }

        try
        {
            // ── 1. Core Accuracy (<= 50m) -> Updates Redis Presence
            const double coreLat = 13.7510;
            const double coreLng = 100.5010;
            const double coreAcc = 20.0;

            var corePayload = new { Latitude = coreLat, Longitude = coreLng, Accuracy = coreAcc, Timestamp = DateTime.UtcNow };
            var reqCore = CreateAuthRequest(HttpMethod.Post, "/api/v1/telemetry/gps", riderToken, corePayload);
            var respCore = await _client.SendAsync(reqCore);
            Assert.Equal(HttpStatusCode.OK, respCore.StatusCode);

            // Verify Redis Presence reflects core position
            using (var scope = _factory.Services.CreateScope())
            {
                var presenceService = scope.ServiceProvider.GetRequiredService<RiderPresenceService>();
                var loc = await presenceService.GetLastKnownLocationAsync(riderId);
                Assert.NotNull(loc);
                Assert.Equal(coreLat, loc.Value.Lat, 4);
                Assert.Equal(coreLng, loc.Value.Lng, 4);
            }

            // Reset rate limiter key for test determinism
            using (var scope = _factory.Services.CreateScope())
            {
                var redis = scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>();
                await redis.GetDatabase().KeyDeleteAsync($"rider_last_gps_limit:rest:{riderId}");
            }

            // ── 2. Degraded Accuracy (50m < acc <= 300m) -> Accepted for UI broadcast but strictly does NOT overwrite Redis
            const double degradedLat = 13.7999;
            const double degradedLng = 100.5999;
            const double degradedAcc = 150.0;

            var degradedPayload = new { Latitude = degradedLat, Longitude = degradedLng, Accuracy = degradedAcc, Timestamp = DateTime.UtcNow };
            var reqDegraded = CreateAuthRequest(HttpMethod.Post, "/api/v1/telemetry/gps", riderToken, degradedPayload);
            var respDegraded = await _client.SendAsync(reqDegraded);
            Assert.Equal(HttpStatusCode.OK, respDegraded.StatusCode);

            // Assert: Redis Presence STILL retains core position, NOT degraded
            using (var scope = _factory.Services.CreateScope())
            {
                var presenceService = scope.ServiceProvider.GetRequiredService<RiderPresenceService>();
                var loc = await presenceService.GetLastKnownLocationAsync(riderId);
                Assert.NotNull(loc);
                Assert.Equal(coreLat, loc.Value.Lat, 4);
                Assert.Equal(coreLng, loc.Value.Lng, 4);
            }

            // Reset rate limiter key for test determinism
            using (var scope = _factory.Services.CreateScope())
            {
                var redis = scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>();
                await redis.GetDatabase().KeyDeleteAsync($"rider_last_gps_limit:rest:{riderId}");
            }

            // ── 3. Unusable Accuracy (> 300m) -> Dropped immediately, Redis preserved
            const double unusableLat = 13.8888;
            const double unusableLng = 100.8888;
            const double unusableAcc = 350.0;

            var unusablePayload = new { Latitude = unusableLat, Longitude = unusableLng, Accuracy = unusableAcc, Timestamp = DateTime.UtcNow };
            var reqUnusable = CreateAuthRequest(HttpMethod.Post, "/api/v1/telemetry/gps", riderToken, unusablePayload);
            var respUnusable = await _client.SendAsync(reqUnusable);
            Assert.Equal(HttpStatusCode.OK, respUnusable.StatusCode);

            // Assert: Redis Presence STILL retains core position intact
            using (var scope = _factory.Services.CreateScope())
            {
                var presenceService = scope.ServiceProvider.GetRequiredService<RiderPresenceService>();
                var loc = await presenceService.GetLastKnownLocationAsync(riderId);
                Assert.NotNull(loc);
                Assert.Equal(coreLat, loc.Value.Lat, 4);
                Assert.Equal(coreLng, loc.Value.Lng, 4);
            }
        }
        finally
        {
            // Spatial & rate limiter cleanup
            using var cleanupScope = _factory.Services.CreateScope();
            var presenceService = cleanupScope.ServiceProvider.GetRequiredService<RiderPresenceService>();
            var redis = cleanupScope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>();
            await presenceService.RemoveRiderAsync(riderId);
            await redis.GetDatabase().KeyDeleteAsync($"rider_last_gps_limit:rest:{riderId}");
        }
    }

    // ─── Test E3: Order Route History PostGIS Query & Ordering ────────────────────────

    [Fact]
    public async Task SubStep_3_2_E3_OrderRouteHistory_QueriesPostgisLedger_FiltersAndOrdersAscending()
    {
        var (adminToken, _) = await RegisterUserAsync(AuthConstants.AdminRole);
        var targetRiderId = $"rider_e3_target_{Guid.NewGuid():N}";
        var otherRiderId = $"rider_e3_other_{Guid.NewGuid():N}";
        var shopId = $"shop_e3_{Guid.NewGuid():N}";
        var orderId = $"ord_e3_{Guid.NewGuid():N}";

        var baseTime = DateTime.UtcNow.AddMinutes(-30);
        var assignedAt = baseTime.AddMinutes(-15);
        var completedAt = baseTime.AddMinutes(-5);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // Seed Riders
            db.Riders.Add(new Rider { Id = targetRiderId, Name = "Route Rider E3", State = RiderState.IDLE });
            db.Riders.Add(new Rider { Id = otherRiderId, Name = "Other Rider E3", State = RiderState.IDLE });

            // Seed Shop
            db.Shops.Add(new Shop
            {
                Id = shopId,
                Name = "E3 Bistro",
                IsOpen = true,
                Location = GeoFactory.CreatePoint(new Coordinate(100.500, 13.750))
            });

            // Seed Order
            var order = new Order
            {
                Id = orderId,
                RefNumber = 3001,
                ShopId = shopId,
                AssignedRiderId = targetRiderId,
                State = OrderState.COMPLETED,
                CreatedAt = baseTime.AddMinutes(-20),
                AssignedAt = assignedAt,
                CompletedAt = completedAt,
                DeliveryAddress = "456 Sukhumvit Road",
                DistanceKm = 4.2,
                DeliveryFee = 55.00m,
                EncodedPolyline = "mock_encoded_polyline_e3",
                PickupLocation = GeoFactory.CreatePoint(new Coordinate(100.500, 13.750)),
                DropoffLocation = GeoFactory.CreatePoint(new Coordinate(100.530, 13.780))
            };
            db.Orders.Add(order);

            // ── Seed PostGIS Telemetry Points in RiderLocationHistories ──
            // Window: from = assignedAt (-15m), to = completedAt (-5m)
            // queryFrom = from - 5m (-20m), queryTo = to + 2m (-3m)

            // Point 1: Valid (target rider, in window: -12m)
            db.RiderLocationHistories.Add(new RiderLocationHistory
            {
                Id = Guid.NewGuid().ToString("N"),
                RiderId = targetRiderId,
                Location = GeoFactory.CreatePoint(new Coordinate(100.505, 13.755)), // X = Lng, Y = Lat
                RecordedAt = baseTime.AddMinutes(-12),
                OrderId = orderId
            });

            // Point 2: Valid (target rider, in window: -8m)
            db.RiderLocationHistories.Add(new RiderLocationHistory
            {
                Id = Guid.NewGuid().ToString("N"),
                RiderId = targetRiderId,
                Location = GeoFactory.CreatePoint(new Coordinate(100.515, 13.765)),
                RecordedAt = baseTime.AddMinutes(-8),
                OrderId = orderId
            });

            // Point 3: Valid (target rider, in window: -6m)
            db.RiderLocationHistories.Add(new RiderLocationHistory
            {
                Id = Guid.NewGuid().ToString("N"),
                RiderId = targetRiderId,
                Location = GeoFactory.CreatePoint(new Coordinate(100.525, 13.775)),
                RecordedAt = baseTime.AddMinutes(-6),
                OrderId = orderId
            });

            // Point 4: Wrong Rider (same window: -10m) -> MUST BE EXCLUDED
            db.RiderLocationHistories.Add(new RiderLocationHistory
            {
                Id = Guid.NewGuid().ToString("N"),
                RiderId = otherRiderId,
                Location = GeoFactory.CreatePoint(new Coordinate(100.999, 13.999)),
                RecordedAt = baseTime.AddMinutes(-10),
                OrderId = null
            });

            // Point 5: Before Query Window (-25m) -> MUST BE EXCLUDED
            db.RiderLocationHistories.Add(new RiderLocationHistory
            {
                Id = Guid.NewGuid().ToString("N"),
                RiderId = targetRiderId,
                Location = GeoFactory.CreatePoint(new Coordinate(100.111, 13.111)),
                RecordedAt = baseTime.AddMinutes(-25),
                OrderId = null
            });

            // Point 6: After Query Window (0m) -> MUST BE EXCLUDED
            db.RiderLocationHistories.Add(new RiderLocationHistory
            {
                Id = Guid.NewGuid().ToString("N"),
                RiderId = targetRiderId,
                Location = GeoFactory.CreatePoint(new Coordinate(100.222, 13.222)),
                RecordedAt = baseTime.AddMinutes(0),
                OrderId = null
            });

            await db.SaveChangesAsync();
        }

        var req = CreateAuthRequest(HttpMethod.Get, $"/api/v1/orders/{orderId}/route-history", adminToken);
        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var result = await resp.Content.ReadFromJsonAsync<ApiResponseWrapper<OrderRouteHistoryDto>>(_jsonOpts);
        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.NotNull(result.Value);

        var dto = result.Value;
        Assert.Equal(orderId, dto.OrderId);
        Assert.Equal("ORD-003001", dto.TrackingCode);
        Assert.Equal("E3 Bistro", dto.ShopName);
        Assert.Equal("456 Sukhumvit Road", dto.DeliveryAddress);
        Assert.Equal(targetRiderId, dto.AssignedRiderId);
        Assert.Equal("Route Rider E3", dto.RiderName);
        Assert.Equal(4.2, dto.DistanceKm);
        Assert.Equal(55.00m, dto.DeliveryFee);
        Assert.Equal("mock_encoded_polyline_e3", dto.PlannedPolyline);

        // Assert: Pickup / Dropoff Coordinates
        Assert.NotNull(dto.PickupLat);
        Assert.NotNull(dto.PickupLng);
        Assert.Equal(13.750, dto.PickupLat.Value, 3);
        Assert.Equal(100.500, dto.PickupLng.Value, 3);

        Assert.NotNull(dto.DropoffLat);
        Assert.NotNull(dto.DropoffLng);
        Assert.Equal(13.780, dto.DropoffLat.Value, 3);
        Assert.Equal(100.530, dto.DropoffLng.Value, 3);

        // Assert: Exactly 3 points returned (Filtered out other rider & out-of-window points)
        Assert.NotNull(dto.ActualGpsPoints);
        Assert.Equal(3, dto.ActualGpsPoints.Count);

        // Assert: Chronological Ascending Order
        var points = dto.ActualGpsPoints;
        Assert.True(points[0].RecordedAt < points[1].RecordedAt, "Point 0 must precede Point 1");
        Assert.True(points[1].RecordedAt < points[2].RecordedAt, "Point 1 must precede Point 2");

        // Assert: Explicit Coordinate Mapping (Lat = Point.Y, Lng = Point.X)
        Assert.Equal(13.755, points[0].Lat, 3); // Y
        Assert.Equal(100.505, points[0].Lng, 3); // X

        Assert.Equal(13.765, points[1].Lat, 3);
        Assert.Equal(100.515, points[1].Lng, 3);

        Assert.Equal(13.775, points[2].Lat, 3);
        Assert.Equal(100.525, points[2].Lng, 3);
    }

    // ─── Test E4: Route History Security & Edge Cases ─────────────────────────────────

    [Fact]
    public async Task SubStep_3_2_E4_RouteHistorySecurityAndEdgeCases_EnforcesContracts()
    {
        var (adminToken, _) = await RegisterUserAsync(AuthConstants.AdminRole);
        var (custToken, _) = await RegisterUserAsync(AuthConstants.CustomerRole);
        var (riderToken, _) = await RegisterUserAsync(AuthConstants.RiderRole);

        var shopId = $"shop_e4_{Guid.NewGuid():N}";
        var unassignedOrderId = $"ord_e4_unassigned_{Guid.NewGuid():N}";
        var assignedOrderId = $"ord_e4_assigned_{Guid.NewGuid():N}";

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            db.Shops.Add(new Shop
            {
                Id = shopId,
                Name = "E4 Guard Shop",
                IsOpen = true
            });

            // Order with NO assigned rider
            db.Orders.Add(new Order
            {
                Id = unassignedOrderId,
                RefNumber = 4001,
                ShopId = shopId,
                AssignedRiderId = null,
                State = OrderState.CREATED
            });

            // Order WITH assigned rider
            db.Orders.Add(new Order
            {
                Id = assignedOrderId,
                RefNumber = 4002,
                ShopId = shopId,
                AssignedRiderId = "some_rider_id",
                State = OrderState.ASSIGNED
            });

            await db.SaveChangesAsync();
        }

        // ── 1. Non-existent Order -> 404 Not Found (NOT_FOUND)
        var ghostOrderId = $"ord_ghost_{Guid.NewGuid():N}";
        var reqGhost = CreateAuthRequest(HttpMethod.Get, $"/api/v1/orders/{ghostOrderId}/route-history", adminToken);
        var respGhost = await _client.SendAsync(reqGhost);
        Assert.Equal(HttpStatusCode.NotFound, respGhost.StatusCode);
        var errGhost = await respGhost.Content.ReadAsStringAsync();
        Assert.Contains("NOT_FOUND", errGhost);

        // ── 2. Order without Assigned Rider -> 200 OK with empty ActualGpsPoints list []
        var reqUnassigned = CreateAuthRequest(HttpMethod.Get, $"/api/v1/orders/{unassignedOrderId}/route-history", adminToken);
        var respUnassigned = await _client.SendAsync(reqUnassigned);
        Assert.Equal(HttpStatusCode.OK, respUnassigned.StatusCode);

        var resultUnassigned = await respUnassigned.Content.ReadFromJsonAsync<ApiResponseWrapper<OrderRouteHistoryDto>>(_jsonOpts);
        Assert.NotNull(resultUnassigned);
        Assert.True(resultUnassigned.Success);
        Assert.NotNull(resultUnassigned.Value);
        Assert.NotNull(resultUnassigned.Value.ActualGpsPoints);
        Assert.Empty(resultUnassigned.Value.ActualGpsPoints);

        // ── 3. Customer Role -> 403 Forbidden
        var reqCust = CreateAuthRequest(HttpMethod.Get, $"/api/v1/orders/{assignedOrderId}/route-history", custToken);
        var respCust = await _client.SendAsync(reqCust);
        Assert.Equal(HttpStatusCode.Forbidden, respCust.StatusCode);

        // ── 4. Rider Role -> 403 Forbidden
        var reqRider = CreateAuthRequest(HttpMethod.Get, $"/api/v1/orders/{assignedOrderId}/route-history", riderToken);
        var respRider = await _client.SendAsync(reqRider);
        Assert.Equal(HttpStatusCode.Forbidden, respRider.StatusCode);

        // ── 5. Unauthenticated Request -> 401 Unauthorized
        var reqAnon = CreateAuthRequest(HttpMethod.Get, $"/api/v1/orders/{assignedOrderId}/route-history", token: null);
        var respAnon = await _client.SendAsync(reqAnon);
        Assert.Equal(HttpStatusCode.Unauthorized, respAnon.StatusCode);
    }
}

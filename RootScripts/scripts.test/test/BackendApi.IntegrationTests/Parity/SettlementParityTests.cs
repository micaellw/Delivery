using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using BackendApi.Controllers.Shops;
using BackendApi.Core.Constants;
using BackendApi.Core.Models.Response;
using BackendApi.Core.StateMachines;
using BackendApi.Data;
using BackendApi.Models.DTOs;
using BackendApi.Models.Entities;
using BackendApi.Security.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using Xunit;
using Order = BackendApi.Models.Entities.Order;

namespace BackendApi.IntegrationTests.Parity;

/// <summary>
/// Sub-step 3.2-D: Settlement Functional Parity Integration Tests
/// Verifies:
/// 1. Rider Completed Orders / Trip Ledger (GET /api/v1/riders/{riderId}/completed-orders)
///    - Filters strictly to COMPLETED orders for the specified rider within the explicit time range
///    - Returns DeliveryFee, DistanceKm, pickup/dropoff coordinates, and timestamps.
/// 2. Validation & Security Boundaries for Rider Completed Orders
///    - Time-range validation (from >= to -> 400, span > 31 days -> 400, rider not found -> 404)
///    - Authorization guards: OperationsPolicy enforces 200 for Admin/Dispatcher, 403 for Customer/Rider.
/// 3. Shop Sales Summary (GET /api/v1/shops/{shopId}/reports/summary)
///    - Calculates TotalOrders, CompletedOrders, CancelledOrders
///    - TotalRevenue = sum(OrderItems of non-cancelled orders); DeliveryFee strictly excluded from shop revenue
///    - Computes AverageOrderValue, TopItems, and detailed order breakdown.
/// 4. Shop Sales Report CSV Export (GET /api/v1/shops/{shopId}/reports/export)
///    - Returns Content-Type text/csv with UTF-8 BOM preamble
///    - Attachment filename matches store-report-{shopId8}-{period}-{date}.csv
///    - Contains executive summary header and detailed breakdown table matching summary figures.
/// </summary>
[Collection("SharedTestDatabase")]
public class SettlementParityTests : IAsyncLifetime
{
    private readonly DeliveryWebApplicationFactory _factory;
    private HttpClient _client = default!;
    private readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };

    public SettlementParityTests(DeliveryWebApplicationFactory factory)
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

        var email = $"{role.ToLowerInvariant()}_settlement_{Guid.NewGuid():N}@test.com";
        var payload = new RegisterPayload(email, "P@ssword123!", $"Settlement {role}", role);
        var resp = await _client.PostAsJsonAsync("/api/v1/auth/register", payload);
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<ApiResponseWrapper<AuthData>>(_jsonOpts);
        return (body!.Value!.AccessToken, body.Value.User!.Id);
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

    // ─── Test D1: Rider Completed Orders / Trip Ledger ────────────────────────────────

    [Fact]
    public async Task SubStep_3_2_D1_RiderCompletedOrdersLedger_CalculatesFeesAndFiltersCompletedOnly()
    {
        var (adminToken, _) = await RegisterUserAsync(AuthConstants.AdminRole);
        var targetRiderId = $"rider_d1_{Guid.NewGuid():N}";
        var otherRiderId = $"rider_other_d1_{Guid.NewGuid():N}";
        var shopId = $"shop_d1_{Guid.NewGuid():N}";

        var baseTime = DateTime.UtcNow.AddMinutes(-30);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // Seed Riders
            db.Riders.Add(new Rider { Id = targetRiderId, Name = "Target D1 Rider", State = RiderState.IDLE });
            db.Riders.Add(new Rider { Id = otherRiderId, Name = "Other D1 Rider", State = RiderState.IDLE });

            // Seed Shop
            db.Shops.Add(new Shop
            {
                Id = shopId,
                Name = "D1 Gourmet Shop",
                IsOpen = true,
                Location = GeoFactory.CreatePoint(new Coordinate(100.501, 13.751))
            });

            // Order 1: Target Rider, COMPLETED, in range
            var order1 = new Order
            {
                Id = $"ord_d1_1_{Guid.NewGuid():N}",
                RefNumber = 1001,
                ShopId = shopId,
                AssignedRiderId = targetRiderId,
                State = OrderState.COMPLETED,
                CreatedAt = baseTime.AddMinutes(-25),
                AssignedAt = baseTime.AddMinutes(-20),
                CompletedAt = baseTime.AddMinutes(-10),
                DeliveryFee = 45.50m,
                DistanceKm = 3.5,
                PickupLocation = GeoFactory.CreatePoint(new Coordinate(100.501, 13.751)),
                DropoffLocation = GeoFactory.CreatePoint(new Coordinate(100.521, 13.771)),
                DeliveryAddress = "123 Test Street",
                Rating = 5
            };
            db.Orders.Add(order1);

            // Order 2: Target Rider, DELIVERING (Not Completed yet -> must be excluded)
            var order2 = new Order
            {
                Id = $"ord_d1_2_{Guid.NewGuid():N}",
                RefNumber = 1002,
                ShopId = shopId,
                AssignedRiderId = targetRiderId,
                State = OrderState.DELIVERING,
                CreatedAt = baseTime.AddMinutes(-15),
                AssignedAt = baseTime.AddMinutes(-5),
                CompletedAt = null,
                DeliveryFee = 50.00m,
                DistanceKm = 4.0
            };
            db.Orders.Add(order2);

            // Order 3: Other Rider, COMPLETED -> must be excluded
            var order3 = new Order
            {
                Id = $"ord_d1_3_{Guid.NewGuid():N}",
                RefNumber = 1003,
                ShopId = shopId,
                AssignedRiderId = otherRiderId,
                State = OrderState.COMPLETED,
                CreatedAt = baseTime.AddMinutes(-20),
                AssignedAt = baseTime.AddMinutes(-18),
                CompletedAt = baseTime.AddMinutes(-8),
                DeliveryFee = 60.00m,
                DistanceKm = 5.0
            };
            db.Orders.Add(order3);

            await db.SaveChangesAsync();
        }

        // Explicit from/to window encompassing order1
        var fromUtc = Uri.EscapeDataString(baseTime.AddHours(-1).ToString("o"));
        var toUtc = Uri.EscapeDataString(baseTime.AddHours(1).ToString("o"));
        var url = $"/api/v1/riders/{targetRiderId}/completed-orders?from={fromUtc}&to={toUtc}&limit=50";

        var req = CreateAuthRequest(HttpMethod.Get, url, adminToken);
        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var result = await resp.Content.ReadFromJsonAsync<ApiResponseWrapper<List<RiderCompletedOrderDto>>>(_jsonOpts);
        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.NotNull(result.Value);

        // Assert: Exactly 1 order returned (Order 1)
        var orders = result.Value;
        Assert.Single(orders);

        var item = orders[0];
        Assert.Equal("ORD-001001", item.TrackingCode);
        Assert.Equal("D1 Gourmet Shop", item.ShopName);
        Assert.Equal("123 Test Street", item.DeliveryAddress);
        Assert.Equal(45.50m, item.DeliveryFee);
        Assert.Equal(3.5, item.DistanceKm);
        Assert.NotNull(item.PickupLat);
        Assert.NotNull(item.PickupLng);
        Assert.Equal(13.751, item.PickupLat.Value, 3);
        Assert.Equal(100.501, item.PickupLng.Value, 3);
        Assert.NotNull(item.DropoffLat);
        Assert.NotNull(item.DropoffLng);
        Assert.Equal(13.771, item.DropoffLat.Value, 3);
        Assert.Equal(100.521, item.DropoffLng.Value, 3);
        Assert.NotNull(item.CompletedAt);
        Assert.Equal(5, item.Rating);
    }

    // ─── Test D2: Time-range validation & Authorization ───────────────────────────────

    [Fact]
    public async Task SubStep_3_2_D2_RiderCompletedOrders_TimeRangeValidationAndAuthorizationGuard()
    {
        var (adminToken, _) = await RegisterUserAsync(AuthConstants.AdminRole);
        var (riderToken, _) = await RegisterUserAsync(AuthConstants.RiderRole);
        var (custToken, _) = await RegisterUserAsync(AuthConstants.CustomerRole);

        var riderId = $"rider_d2_{Guid.NewGuid():N}";
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Riders.Add(new Rider { Id = riderId, Name = "D2 Validation Rider", State = RiderState.IDLE });
            await db.SaveChangesAsync();
        }

        var validFrom = Uri.EscapeDataString(DateTime.UtcNow.AddDays(-1).ToString("o"));
        var validTo = Uri.EscapeDataString(DateTime.UtcNow.ToString("o"));

        // ── Validation 1: from >= to -> 400 Bad Request
        var badFrom = Uri.EscapeDataString(DateTime.UtcNow.ToString("o"));
        var badTo = Uri.EscapeDataString(DateTime.UtcNow.AddDays(-1).ToString("o"));
        var req1 = CreateAuthRequest(HttpMethod.Get, $"/api/v1/riders/{riderId}/completed-orders?from={badFrom}&to={badTo}", adminToken);
        var resp1 = await _client.SendAsync(req1);
        Assert.Equal(HttpStatusCode.BadRequest, resp1.StatusCode);
        var err1 = await resp1.Content.ReadAsStringAsync();
        Assert.Contains("INVALID_TIME_RANGE", err1);

        // ── Validation 2: Time range > 31 days -> 400 Bad Request
        var range45DaysFrom = Uri.EscapeDataString(DateTime.UtcNow.AddDays(-45).ToString("o"));
        var req2 = CreateAuthRequest(HttpMethod.Get, $"/api/v1/riders/{riderId}/completed-orders?from={range45DaysFrom}&to={validTo}", adminToken);
        var resp2 = await _client.SendAsync(req2);
        Assert.Equal(HttpStatusCode.BadRequest, resp2.StatusCode);
        var err2 = await resp2.Content.ReadAsStringAsync();
        Assert.Contains("TIME_RANGE_TOO_LARGE", err2);

        // ── Validation 3: Non-existent rider -> 404 Not Found
        var nonExistentId = $"rider_ghost_{Guid.NewGuid():N}";
        var req3 = CreateAuthRequest(HttpMethod.Get, $"/api/v1/riders/{nonExistentId}/completed-orders?from={validFrom}&to={validTo}", adminToken);
        var resp3 = await _client.SendAsync(req3);
        Assert.Equal(HttpStatusCode.NotFound, resp3.StatusCode);

        // ── Authorization 4: OperationsPolicy blocks Customer (403) and Rider (403)
        var custReq = CreateAuthRequest(HttpMethod.Get, $"/api/v1/riders/{riderId}/completed-orders?from={validFrom}&to={validTo}", custToken);
        var custResp = await _client.SendAsync(custReq);
        Assert.Equal(HttpStatusCode.Forbidden, custResp.StatusCode);

        var riderReq = CreateAuthRequest(HttpMethod.Get, $"/api/v1/riders/{riderId}/completed-orders?from={validFrom}&to={validTo}", riderToken);
        var riderResp = await _client.SendAsync(riderReq);
        Assert.Equal(HttpStatusCode.Forbidden, riderResp.StatusCode);

        // ── Authorization 5: Admin succeeds (200 OK)
        var adminReq = CreateAuthRequest(HttpMethod.Get, $"/api/v1/riders/{riderId}/completed-orders?from={validFrom}&to={validTo}", adminToken);
        var adminResp = await _client.SendAsync(adminReq);
        Assert.Equal(HttpStatusCode.OK, adminResp.StatusCode);
    }

    // ─── Test D3: Shop Sales Summary & Revenue Calculations ───────────────────────────

    [Fact]
    public async Task SubStep_3_2_D3_ShopSalesSummary_CalculatesRevenueAndOrderBreakdownAccurately()
    {
        var (authToken, userId) = await RegisterUserAsync(AuthConstants.CustomerRole);
        var shopId = $"shop_d3_{Guid.NewGuid():N}";
        var targetDate = new DateTime(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);
        var orderTime = new DateTime(2026, 9, 22, 10, 0, 0, DateTimeKind.Utc);

        var menuItem1Id = $"m_d3_1_{Guid.NewGuid():N}";
        var menuItem2Id = $"m_d3_2_{Guid.NewGuid():N}";
        var menuItem3Id = $"m_d3_3_{Guid.NewGuid():N}";

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            db.Shops.Add(new Shop
            {
                Id = shopId,
                Name = "D3 Noodle Bar",
                IsOpen = true
            });

            // Seed MenuItems first to satisfy Foreign Key constraints
            db.MenuItems.Add(new MenuItem { Id = menuItem1Id, ShopId = shopId, Name = "Pad Thai", Price = 80.00m });
            db.MenuItems.Add(new MenuItem { Id = menuItem2Id, ShopId = shopId, Name = "Thai Tea", Price = 35.00m });
            db.MenuItems.Add(new MenuItem { Id = menuItem3Id, ShopId = shopId, Name = "Spring Rolls", Price = 60.00m });

            // Order 1: COMPLETED (Pad Thai x2 @80 = 160, Thai Tea x1 @35 = 35) -> Subtotal = 195, DeliveryFee = 40
            var order1 = new Order
            {
                Id = $"ord_d3_1_{Guid.NewGuid():N}",
                RefNumber = 101,
                ShopId = shopId,
                CustomerId = userId,
                State = OrderState.COMPLETED,
                CreatedAt = orderTime,
                DeliveryFee = 40.00m,
                Items = new List<OrderItem>
                {
                    new() { Id = Guid.NewGuid().ToString("N"), MenuItemId = menuItem1Id, Name = "Pad Thai", UnitPrice = 80.00m, Quantity = 2 },
                    new() { Id = Guid.NewGuid().ToString("N"), MenuItemId = menuItem2Id, Name = "Thai Tea", UnitPrice = 35.00m, Quantity = 1 }
                }
            };
            db.Orders.Add(order1);

            // Order 2: DELIVERING (Pad Thai x1 @80 = 80) -> Subtotal = 80, DeliveryFee = 35
            var order2 = new Order
            {
                Id = $"ord_d3_2_{Guid.NewGuid():N}",
                RefNumber = 102,
                ShopId = shopId,
                CustomerId = userId,
                State = OrderState.DELIVERING,
                CreatedAt = orderTime.AddMinutes(15),
                DeliveryFee = 35.00m,
                Items = new List<OrderItem>
                {
                    new() { Id = Guid.NewGuid().ToString("N"), MenuItemId = menuItem1Id, Name = "Pad Thai", UnitPrice = 80.00m, Quantity = 1 }
                }
            };
            db.Orders.Add(order2);

            // Order 3: CANCELLED (Spring Rolls x2 @60 = 120) -> Subtotal = 120 (Cancelled -> excluded from revenue)
            var order3 = new Order
            {
                Id = $"ord_d3_3_{Guid.NewGuid():N}",
                RefNumber = 103,
                ShopId = shopId,
                CustomerId = userId,
                State = OrderState.CANCELLED,
                CreatedAt = orderTime.AddMinutes(30),
                DeliveryFee = 30.00m,
                Items = new List<OrderItem>
                {
                    new() { Id = Guid.NewGuid().ToString("N"), MenuItemId = menuItem3Id, Name = "Spring Rolls", UnitPrice = 60.00m, Quantity = 2 }
                }
            };
            db.Orders.Add(order3);

            await db.SaveChangesAsync();
        }

        var dateParam = Uri.EscapeDataString(targetDate.ToString("o"));
        var req = CreateAuthRequest(HttpMethod.Get, $"/api/v1/shops/{shopId}/reports/summary?period=day&date={dateParam}", authToken);
        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var result = await resp.Content.ReadFromJsonAsync<ApiResponseWrapper<StoreReportsController.StoreReportSummaryDto>>(_jsonOpts);
        Assert.NotNull(result);
        Assert.True(result.Success);
        Assert.NotNull(result.Value);

        var summary = result.Value;
        Assert.Equal(shopId, summary.ShopId);
        Assert.Equal("D3 Noodle Bar", summary.ShopName);
        Assert.Equal("day", summary.Period);

        // Verify Order Counts
        Assert.Equal(3, summary.TotalOrders);         // Order 1, 2, 3
        Assert.Equal(1, summary.CompletedOrders);     // Order 1
        Assert.Equal(1, summary.CancelledOrders);     // Order 3

        // Verify Revenue: Subtotals of Order 1 (195) + Order 2 (80) = 275.00m (DeliveryFee strictly excluded!)
        Assert.Equal(275.00m, summary.TotalRevenue);
        Assert.Equal(137.50m, summary.AverageOrderValue); // 275 / 2

        // Verify Top Items
        Assert.Equal(2, summary.TopItems.Count);
        var top1 = summary.TopItems.First(x => x.Name == "Pad Thai");
        Assert.Equal(3, top1.Quantity);
        Assert.Equal(240.00m, top1.TotalAmount);

        var top2 = summary.TopItems.First(x => x.Name == "Thai Tea");
        Assert.Equal(1, top2.Quantity);
        Assert.Equal(35.00m, top2.TotalAmount);

        // Verify Detailed Order Breakdown includes all 3 orders
        Assert.Equal(3, summary.Orders.Count);
    }

    // ─── Test D4: Shop Report CSV Export & Data Consistency ───────────────────────────

    [Fact]
    public async Task SubStep_3_2_D4_ShopReportExportCsv_StreamsUtf8BomAndMatchesSummaryData()
    {
        var (authToken, userId) = await RegisterUserAsync(AuthConstants.CustomerRole);
        var shopId = $"shop_d4_{Guid.NewGuid():N}";
        var targetDate = new DateTime(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);
        var orderTime = new DateTime(2026, 9, 22, 11, 0, 0, DateTimeKind.Utc);

        var menuItem1Id = $"m_d4_1_{Guid.NewGuid():N}";
        var menuItem2Id = $"m_d4_2_{Guid.NewGuid():N}";

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            db.Shops.Add(new Shop
            {
                Id = shopId,
                Name = "D4 Seafood Diner",
                IsOpen = true
            });

            // Seed MenuItems first to satisfy Foreign Key constraints
            db.MenuItems.Add(new MenuItem { Id = menuItem1Id, ShopId = shopId, Name = "Tom Yum", Price = 150.00m });
            db.MenuItems.Add(new MenuItem { Id = menuItem2Id, ShopId = shopId, Name = "Fried Rice", Price = 70.00m });

            // Order 1: COMPLETED, Tom Yum x1 @150
            db.Orders.Add(new Order
            {
                Id = $"ord_d4_1_{Guid.NewGuid():N}",
                RefNumber = 201,
                ShopId = shopId,
                CustomerId = userId,
                State = OrderState.COMPLETED,
                CreatedAt = orderTime,
                DeliveryFee = 50.00m,
                Items = new List<OrderItem>
                {
                    new() { Id = Guid.NewGuid().ToString("N"), MenuItemId = menuItem1Id, Name = "Tom Yum", UnitPrice = 150.00m, Quantity = 1 }
                }
            });

            // Order 2: CANCELLED, Fried Rice x1 @70
            db.Orders.Add(new Order
            {
                Id = $"ord_d4_2_{Guid.NewGuid():N}",
                RefNumber = 202,
                ShopId = shopId,
                CustomerId = userId,
                State = OrderState.CANCELLED,
                CreatedAt = orderTime.AddMinutes(20),
                DeliveryFee = 30.00m,
                Items = new List<OrderItem>
                {
                    new() { Id = Guid.NewGuid().ToString("N"), MenuItemId = menuItem2Id, Name = "Fried Rice", UnitPrice = 70.00m, Quantity = 1 }
                }
            });

            await db.SaveChangesAsync();
        }

        var dateParam = Uri.EscapeDataString(targetDate.ToString("o"));
        var req = CreateAuthRequest(HttpMethod.Get, $"/api/v1/shops/{shopId}/reports/export?period=day&date={dateParam}&format=csv", authToken);
        var resp = await _client.SendAsync(req);

        // ── 1. HTTP Headers Check
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/csv", resp.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", resp.Content.Headers.ContentType?.CharSet);

        var disposition = resp.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        Assert.Contains($"store-report-{shopId.Substring(0, 8)}-day-20260922.csv", disposition.FileName);

        // ── 2. Raw Bytes & UTF-8 BOM Check
        var rawBytes = await resp.Content.ReadAsByteArrayAsync();
        Assert.True(rawBytes.Length > 3);
        Assert.Equal(0xEF, rawBytes[0]);
        Assert.Equal(0xBB, rawBytes[1]);
        Assert.Equal(0xBF, rawBytes[2]);

        // ── 3. CSV Content String & Data Consistency Check
        var csvContent = Encoding.UTF8.GetString(rawBytes, 3, rawBytes.Length - 3);

        // Section 1: Executive Summary
        Assert.Contains("รายงานสรุปยอดขายร้านค้า (Sales Report)", csvContent);
        Assert.Contains("D4 Seafood Diner", csvContent);
        Assert.Contains("\"จำนวนออเดอร์ทั้งหมด:\",\"2\"", csvContent);
        Assert.Contains("\"ออเดอร์จัดส่งสำเร็จ:\",\"1\"", csvContent);
        Assert.Contains("\"ออเดอร์ยกเลิก:\",\"1\"", csvContent);
        Assert.Contains("\"ยอดขายรวมทั้งสิ้น:\",\"150.00 บาท\"", csvContent);

        // Section 2: Detailed Order Breakdown
        Assert.Contains("\"ลำดับ\",\"เลขอ้างอิง\",\"วันที่-เวลา\",\"ลูกค้า\",\"สถานะ\",\"จำนวนรายการ\",\"รายการอาหาร\",\"ยอดเงิน (บาท)\"", csvContent);
        Assert.Contains("#201", csvContent);
        Assert.Contains("Tom Yum", csvContent);
        Assert.Contains("150.00", csvContent);
        Assert.Contains("#202", csvContent);
        Assert.Contains("Fried Rice", csvContent);
        Assert.Contains("70.00", csvContent);

        // ── 4. Default Parameters Check (without query string)
        var defaultReq = CreateAuthRequest(HttpMethod.Get, $"/api/v1/shops/{shopId}/reports/export", authToken);
        var defaultResp = await _client.SendAsync(defaultReq);
        Assert.Equal(HttpStatusCode.OK, defaultResp.StatusCode);
        Assert.Equal("text/csv", defaultResp.Content.Headers.ContentType?.MediaType);
    }
}

using System;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using BackendApi.Data;
using BackendApi.Hubs.Tracking;
using BackendApi.IntegrationTests;
using BackendApi.Models.Entities;
using BackendApi.Security;
using BackendApi.Security.Models;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BackendApi.IntegrationTests.Hubs;

[Collection("SharedTestDatabase")]
public class TrackingHubRegressionIntegrationTests : IAsyncLifetime
{
    private readonly DeliveryWebApplicationFactory _factory;
    private HttpClient _client = default!;

    public TrackingHubRegressionIntegrationTests(DeliveryWebApplicationFactory factory)
    {
        _factory = factory;
    }

    public Task InitializeAsync()
    {
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private HubConnection CreateHubConnection(string? token = null)
    {
        var url = new Uri(_factory.Server.BaseAddress, "/hubs/tracking").ToString();
        var builder = new HubConnectionBuilder()
            .WithUrl(url, options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                if (!string.IsNullOrEmpty(token))
                {
                    options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                }
            });

        return builder.Build();
    }

    private async Task<(string Token, string UserId)> RegisterRiderAsync()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new
            {
                Email = $"rider_hub_{Guid.NewGuid():N}@test.com",
                Password = "TestPass123!",
                FullName = "Hub Test Rider",
                Role = "Rider"
            });
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement.GetProperty("value");
        var token = root.GetProperty("accessToken").GetString()!;
        var riderId = root.GetProperty("user").GetProperty("riderId").GetString()!;
        return (token, riderId);
    }

    [Fact]
    public async Task Case1_UnauthenticatedConnection_ShouldBeRejected()
    {
        await using var connection = CreateHubConnection(token: null);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());
        Assert.NotNull(ex);
    }

    [Fact]
    public async Task Case2_AdminAndRider_CanConnectSuccessfully()
    {
        var (adminToken, _) = await _factory.CreatePrivilegedUserAndGetTokenAsync(_client, AuthConstants.AdminRole);
        await using var adminConn = CreateHubConnection(adminToken);
        await adminConn.StartAsync();
        Assert.Equal(HubConnectionState.Connected, adminConn.State);

        var (riderToken, _) = await RegisterRiderAsync();
        await using var riderConn = CreateHubConnection(riderToken);
        await riderConn.StartAsync();
        Assert.Equal(HubConnectionState.Connected, riderConn.State);
    }

    [Fact]
    public async Task Case3_NormalGpsUpdate_BroadcastsToAdminAndRiderOwnGroup()
    {
        var (adminToken, _) = await _factory.CreatePrivilegedUserAndGetTokenAsync(_client, AuthConstants.AdminRole);
        await using var adminConn = CreateHubConnection(adminToken);
        await adminConn.StartAsync();

        var (riderToken, riderId) = await RegisterRiderAsync();
        await using var riderConn = CreateHubConnection(riderToken);
        await riderConn.StartAsync();

        var adminReceivedTcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        adminConn.On<JsonElement>("RiderLocationUpdated", payload =>
        {
            var pRiderId = payload.GetProperty("riderId").GetString();
            if (pRiderId == riderId)
            {
                adminReceivedTcs.TrySetResult(payload);
            }
        });

        // Rider invokes UpdateLocation with standard accuracy (10.0m <= 50m)
        await riderConn.InvokeAsync("UpdateLocation", 17.4138, 102.7872, 10.0);

        var completedTask = await Task.WhenAny(adminReceivedTcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(adminReceivedTcs.Task, completedTask);

        var result = await adminReceivedTcs.Task;
        Assert.Equal(riderId, result.GetProperty("riderId").GetString());
        Assert.Equal(17.4138, result.GetProperty("lat").GetDouble(), 4);
        Assert.Equal(102.7872, result.GetProperty("lng").GetDouble(), 4);
        Assert.Equal(10.0, result.GetProperty("accuracy").GetDouble(), 1);
    }

    [Fact]
    public async Task Case4_DegradedAccuracy_BroadcastsToAdminOnly()
    {
        var (adminToken, _) = await _factory.CreatePrivilegedUserAndGetTokenAsync(_client, AuthConstants.AdminRole);
        await using var adminConn = CreateHubConnection(adminToken);
        await adminConn.StartAsync();

        var (riderToken, riderId) = await RegisterRiderAsync();
        await using var riderConn = CreateHubConnection(riderToken);
        await riderConn.StartAsync();

        var (otherRiderToken, _) = await RegisterRiderAsync();
        await using var otherRiderConn = CreateHubConnection(otherRiderToken);
        await otherRiderConn.StartAsync();

        var adminReceivedTcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        adminConn.On<JsonElement>("RiderLocationUpdated", payload =>
        {
            if (payload.GetProperty("riderId").GetString() == riderId)
            {
                adminReceivedTcs.TrySetResult(payload);
            }
        });

        bool otherReceived = false;
        otherRiderConn.On<JsonElement>("RiderLocationUpdated", payload =>
        {
            if (payload.GetProperty("riderId").GetString() == riderId)
            {
                otherReceived = true;
            }
        });

        // Degraded accuracy: 120.0m (between 50m and 300m)
        await riderConn.InvokeAsync("UpdateLocation", 17.4150, 102.7880, 120.0);

        var completed = await Task.WhenAny(adminReceivedTcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(adminReceivedTcs.Task, completed);

        var result = await adminReceivedTcs.Task;
        Assert.Equal(riderId, result.GetProperty("riderId").GetString());
        Assert.Equal(120.0, result.GetProperty("accuracy").GetDouble(), 1);

        // Allow 300ms to verify other non-admin listener received nothing
        await Task.Delay(300);
        Assert.False(otherReceived, "Degraded GPS points must NEVER be broadcast to non-admin groups!");
    }

    [Fact]
    public async Task Case5_UnusableAccuracyAbove300m_IsRejectedImmediately()
    {
        var (adminToken, _) = await _factory.CreatePrivilegedUserAndGetTokenAsync(_client, AuthConstants.AdminRole);
        await using var adminConn = CreateHubConnection(adminToken);
        await adminConn.StartAsync();

        var (riderToken, riderId) = await RegisterRiderAsync();
        await using var riderConn = CreateHubConnection(riderToken);
        await riderConn.StartAsync();

        var adminReceived = false;
        adminConn.On<JsonElement>("RiderLocationUpdated", payload =>
        {
            if (payload.GetProperty("riderId").GetString() == riderId)
            {
                adminReceived = true;
            }
        });

        // Accuracy > 300m (unusable)
        await riderConn.InvokeAsync("UpdateLocation", 17.4138, 102.7872, 350.0);

        await Task.Delay(1000);
        Assert.False(adminReceived, "GPS points with accuracy > 300m must be rejected without broadcast.");
    }

    [Fact]
    public async Task Case6_TeleportAnomaly_IsDetectedAndRejected()
    {
        var (adminToken, _) = await _factory.CreatePrivilegedUserAndGetTokenAsync(_client, AuthConstants.AdminRole);
        await using var adminConn = CreateHubConnection(adminToken);
        await adminConn.StartAsync();

        var (riderToken, riderId) = await RegisterRiderAsync();
        await using var riderConn = CreateHubConnection(riderToken);
        await riderConn.StartAsync();

        var firstPointTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var teleportPointReceived = false;

        adminConn.On<JsonElement>("RiderLocationUpdated", payload =>
        {
            if (payload.GetProperty("riderId").GetString() == riderId)
            {
                var lat = payload.GetProperty("lat").GetDouble();
                if (Math.Abs(lat - 17.4138) < 0.001)
                {
                    firstPointTcs.TrySetResult(true);
                }
                else if (Math.Abs(lat - 17.9000) < 0.001)
                {
                    teleportPointReceived = true;
                }
            }
        });

        // 1. Send normal point
        await riderConn.InvokeAsync("UpdateLocation", 17.4138, 102.7872, 10.0);
        await Task.WhenAny(firstPointTcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(firstPointTcs.Task.IsCompletedSuccessfully);

        // 2. Immediately send teleport point (55 km away within milliseconds -> speed > 50 m/s anomaly)
        await riderConn.InvokeAsync("UpdateLocation", 17.9000, 102.7872, 10.0);

        await Task.Delay(1000);
        Assert.False(teleportPointReceived, "Teleport anomaly coordinate must be rejected.");
    }

    [Fact]
    public async Task Case7_OfferReceived_IsDeliveredToRiderGroupExclusively()
    {
        var (rider1Token, rider1Id) = await RegisterRiderAsync();
        await using var rider1Conn = CreateHubConnection(rider1Token);
        await rider1Conn.StartAsync();

        var (rider2Token, rider2Id) = await RegisterRiderAsync();
        await using var rider2Conn = CreateHubConnection(rider2Token);
        await rider2Conn.StartAsync();

        var rider1OfferTcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        rider1Conn.On<JsonElement>("OfferReceived", payload =>
        {
            rider1OfferTcs.TrySetResult(payload);
        });

        bool rider2Received = false;
        rider2Conn.On<JsonElement>("OfferReceived", payload =>
        {
            rider2Received = true;
        });

        // Broadcast offer via IHubContext to rider1's group
        using (var scope = _factory.Services.CreateScope())
        {
            var hubContext = scope.ServiceProvider.GetRequiredService<IHubContext<TrackingHub>>();
            await hubContext.Clients.Group($"rider:{rider1Id}").SendAsync("OfferReceived", new
            {
                offerId = "offer-test-123",
                orderId = "order-test-456",
                offerVersion = 1,
                shopName = "Test Noodle Bar",
                deliveryFee = 45.0
            });
        }

        var completed = await Task.WhenAny(rider1OfferTcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(rider1OfferTcs.Task, completed);

        var offer = await rider1OfferTcs.Task;
        Assert.Equal("offer-test-123", offer.GetProperty("offerId").GetString());
        Assert.Equal("order-test-456", offer.GetProperty("orderId").GetString());

        await Task.Delay(300);
        Assert.False(rider2Received, "OfferReceived must be delivered exclusively to target rider group!");
    }
}

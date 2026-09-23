using System;
using System.Collections.Generic;
using BackendApi.Services.BackgroundWorkers.Maintenance;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BackendApi.UnitTests.Maintenance;

public class DbMaintenanceWorkerTests
{
    private readonly Mock<IServiceProvider> _serviceProviderMock = new();
    private readonly Mock<ILogger<DbMaintenanceWorker>> _loggerMock = new();

    [Fact]
    public void RetentionDays_WhenNoConfigProvided_ShouldDefaultTo7Days()
    {
        // Arrange
        var configuration = new ConfigurationBuilder().Build();
        var worker = new DbMaintenanceWorker(_serviceProviderMock.Object, configuration, _loggerMock.Object);

        // Act
        var retentionDays = worker.RetentionDays;

        // Assert
        Assert.Equal(7, retentionDays);
    }

    [Fact]
    public void RetentionDays_WhenConfiguredViaEnvironmentVariable_ShouldUseConfiguredValue()
    {
        // Arrange
        var inMemorySettings = new Dictionary<string, string?>
        {
            { "IDEMPOTENCY_RETENTION_DAYS", "14" }
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        var worker = new DbMaintenanceWorker(_serviceProviderMock.Object, configuration, _loggerMock.Object);

        // Act
        var retentionDays = worker.RetentionDays;

        // Assert
        Assert.Equal(14, retentionDays);
    }

    [Fact]
    public void RetentionDays_WhenConfiguredViaAppsettings_ShouldUseConfiguredValue()
    {
        // Arrange
        var inMemorySettings = new Dictionary<string, string?>
        {
            { "Idempotency:RetentionDays", "30" }
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        var worker = new DbMaintenanceWorker(_serviceProviderMock.Object, configuration, _loggerMock.Object);

        // Act
        var retentionDays = worker.RetentionDays;

        // Assert
        Assert.Equal(30, retentionDays);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("not-a-number")]
    [InlineData("")]
    public void RetentionDays_WhenInvalidOrNonPositive_ShouldFallbackToDefault7Days(string invalidValue)
    {
        // Arrange
        var inMemorySettings = new Dictionary<string, string?>
        {
            { "IDEMPOTENCY_RETENTION_DAYS", invalidValue }
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        var worker = new DbMaintenanceWorker(_serviceProviderMock.Object, configuration, _loggerMock.Object);

        // Act
        var retentionDays = worker.RetentionDays;

        // Assert
        Assert.Equal(7, retentionDays);
    }
}

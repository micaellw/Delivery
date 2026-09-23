using BackendApi.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackendApi.Services.BackgroundWorkers.Maintenance;

public class DbMaintenanceWorker : BackgroundService
{
    public const int DefaultRetentionDays = 7;

    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DbMaintenanceWorker> _logger;

    public DbMaintenanceWorker(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<DbMaintenanceWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Retention window in days for ProcessedEvents table.
    /// Configurable via IDEMPOTENCY_RETENTION_DAYS or Idempotency:RetentionDays. Default is 7 days.
    /// </summary>
    public int RetentionDays
    {
        get
        {
            var configValue = _configuration["IDEMPOTENCY_RETENTION_DAYS"]
                ?? _configuration["Idempotency:RetentionDays"];

            if (int.TryParse(configValue, out var days) && days > 0)
            {
                return days;
            }
            return DefaultRetentionDays;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DbMaintenanceWorker started (Idempotency Retention: {RetentionDays} days)", RetentionDays);

        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await PruneProcessedEventsAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error executing database maintenance tasks");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown
        }

        _logger.LogInformation("DbMaintenanceWorker stopped");
    }

    /// <summary>
    /// Prunes stale ProcessedEvents records older than the configured retention window.
    /// Strictly operates only on the ProcessedEvents table without altering any business data.
    /// </summary>
    public async Task<int> PruneProcessedEventsAsync(CancellationToken ct = default, int? retentionDaysOverride = null)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var days = retentionDaysOverride ?? RetentionDays;
        var cutoff = DateTime.UtcNow.AddDays(-days);

        var deletedCount = await dbContext.ProcessedEvents
            .Where(pe => pe.ProcessedAt < cutoff)
            .ExecuteDeleteAsync(ct);

        if (deletedCount > 0)
        {
            _logger.LogInformation("Pruned {Count} stale processed events from database (Retention: {Days} days, Cutoff: {Cutoff})",
                deletedCount, days, cutoff);
        }
        else
        {
            _logger.LogDebug("No stale processed events found to prune (Retention: {Days} days, Cutoff: {Cutoff})",
                days, cutoff);
        }

        return deletedCount;
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackendApi.Services.Storage
{
    public class MinioBucketInitializerHostedService : IHostedService
    {
        private readonly IStorageService _storageService;
        private readonly ILogger<MinioBucketInitializerHostedService> _logger;

        public MinioBucketInitializerHostedService(IStorageService storageService, ILogger<MinioBucketInitializerHostedService> logger)
        {
            _storageService = storageService;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("MinioBucketInitializerHostedService: Initializing MinIO bucket asynchronously during application startup...");
            try
            {
                await _storageService.EnsureBucketExistsAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MinioBucketInitializerHostedService: Bucket initialization failed during startup. Application will proceed without blocking.");
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

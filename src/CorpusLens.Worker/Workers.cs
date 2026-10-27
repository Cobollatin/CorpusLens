using Microsoft.EntityFrameworkCore;
using CorpusLens.Infrastructure;

namespace CorpusLens.Worker;

public sealed class IngestionWorker(ILogger<IngestionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        logger.LogInformation("Ingestion worker ready; no source configured.");
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
    }
}
public sealed class BatchPollingWorker(ILogger<BatchPollingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        logger.LogInformation("Batch polling worker ready; no Batch jobs submitted.");
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
    }
}
public sealed class CoverageGateWorker(ILogger<CoverageGateWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        logger.LogInformation("Coverage gate ready; no staged revision.");
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
    }
}
public sealed class AdbKeepAliveWorker(IServiceProvider services, ILogger<AdbKeepAliveWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                var db = scope.ServiceProvider.GetService<CorpusDbContext>();
                if (db is not null)
                {
                    var connection = db.Database.GetDbConnection();
                    await connection.OpenAsync(token);
                    try
                    {
                        await using var command = connection.CreateCommand();
                        command.CommandText = "SELECT 1 FROM DUAL";
                        _ = await command.ExecuteScalarAsync(token);
                    }
                    finally { await connection.CloseAsync(); }
                    logger.LogInformation("ADB keep-alive succeeded.");
                }
            }
            catch (Exception error) { logger.LogError(error, "ADB keep-alive failed."); }
            await Task.Delay(TimeSpan.FromDays(1), token);
        }
    }
}

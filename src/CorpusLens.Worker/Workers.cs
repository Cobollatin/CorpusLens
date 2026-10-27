
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

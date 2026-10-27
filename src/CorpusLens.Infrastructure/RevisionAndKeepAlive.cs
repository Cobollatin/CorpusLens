using System.Data.Common;
using System.Diagnostics;
using CorpusLens.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Oracle.ManagedDataAccess.Client;

namespace CorpusLens.Infrastructure;

public sealed record EmbeddingCompletenessAudit(bool Passed, int Expected, int Present);
public interface IRevisionStore
{
    Task SwitchAtomicallyAsync(string revisionId, CancellationToken token);
    Task QuarantineAsync(string revisionId, CancellationToken token);
}
public sealed class RevisionSwitchoverService(IRevisionStore store)
{
    public async Task<bool> ActivateLexicalAsync(string revisionId, CoverageAuditReport coverage, CancellationToken token = default)
    {
        if (coverage.RevisionId != revisionId || !coverage.Passed)
        { await store.QuarantineAsync(revisionId, token); return false; }
        await store.SwitchAtomicallyAsync(revisionId, token);
        return true;
    }
    public async Task<bool> ActivateAsync(string revisionId, CoverageAuditReport coverage, EmbeddingCompletenessAudit embeddings, CancellationToken token = default)
    {
        if (coverage.RevisionId != revisionId || !coverage.Passed || !embeddings.Passed || embeddings.Present != embeddings.Expected || embeddings.Expected == 0)
        { await store.QuarantineAsync(revisionId, token); return false; }
        await store.SwitchAtomicallyAsync(revisionId, token);
        return true;
    }
}
public sealed class OracleRevisionStore(Func<DbConnection> connectionFactory) : IRevisionStore
{
    public async Task SwitchAtomicallyAsync(string revisionId, CancellationToken token)
    {
        await using var connection = connectionFactory(); await connection.OpenAsync(token);
        await using var tx = await connection.BeginTransactionAsync(token);
        try
        {
            await Execute("UPDATE source_revision SET status='Superseded' WHERE status='Active' AND source_id=(SELECT source_id FROM source_revision WHERE id=:id)");
            await Execute("UPDATE source_revision SET status='Active' WHERE id=:id AND status IN ('Audited','Ready')");
            await tx.CommitAsync(token);
        }
        catch { await tx.RollbackAsync(token); throw; }
        async Task Execute(string sql)
        { await using var cmd = connection.CreateCommand(); if (cmd is OracleCommand oracle) oracle.BindByName = true; cmd.Transaction = tx; cmd.CommandText = sql; OracleCorpusRepositoryParameter(cmd, revisionId); if (await cmd.ExecuteNonQueryAsync(token) != 1 && sql.Contains("SET status='Active'")) throw new InvalidOperationException("Revision is not Audited or Ready."); }
    }
    public async Task QuarantineAsync(string revisionId, CancellationToken token)
    {
        await using var connection = connectionFactory(); await connection.OpenAsync(token);
        await using var cmd = connection.CreateCommand(); cmd.CommandText = "UPDATE source_revision SET status='Quarantined' WHERE id=:id AND status<>'Active'";
        OracleCorpusRepositoryParameter(cmd, revisionId); await cmd.ExecuteNonQueryAsync(token);
    }
    private static void OracleCorpusRepositoryParameter(DbCommand cmd, string id)
    { var p = cmd.CreateParameter(); p.ParameterName = "id"; p.Value = id; cmd.Parameters.Add(p); }
}

public interface IKeepAliveProbe { Task ProbeAsync(CancellationToken token); }
public sealed class OracleKeepAliveProbe(Func<DbConnection> connectionFactory) : IKeepAliveProbe
{
    public async Task ProbeAsync(CancellationToken token)
    {
        await using var connection = connectionFactory(); await connection.OpenAsync(token);
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT 1 FROM DUAL";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(token)) != 1) throw new InvalidDataException("Oracle keep-alive returned an unexpected result.");
    }
}
public sealed class OracleKeepAliveHostedService(IKeepAliveProbe probe, ILogger<OracleKeepAliveHostedService> logger) : BackgroundService
{
    public TimeSpan Interval { get; init; } = TimeSpan.FromHours(24);
    public DateTimeOffset? LastHeartbeatUtc { get; private set; }
    public TimeSpan? LastLatency { get; private set; }
    public async Task<bool> ProbeOnceAsync(CancellationToken token = default)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            await probe.ProbeAsync(token);
            LastHeartbeatUtc = DateTimeOffset.UtcNow; LastLatency = clock.Elapsed;
            logger.LogInformation("Oracle keep-alive succeeded at {HeartbeatUtc} in {LatencyMs} ms", LastHeartbeatUtc, LastLatency.Value.TotalMilliseconds);
            return true;
        }
        catch (Exception error) when (!token.IsCancellationRequested)
        { logger.LogError(error, "Oracle keep-alive failed after {LatencyMs} ms", clock.Elapsed.TotalMilliseconds); return false; }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            failures = await ProbeOnceAsync(stoppingToken) ? 0 : Math.Min(failures + 1, 8);
            var delay = failures == 0 ? Interval : TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, failures)));
            await Task.Delay(delay, stoppingToken);
        }
    }
}

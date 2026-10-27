using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CorpusLens.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using CorpusLens.Infrastructure.Persistence;
using Oracle.ManagedDataAccess.Client;
using System.Data.Common;

namespace CorpusLens.Infrastructure;

public static class InfrastructureServices
{
    public static IServiceCollection AddCorpusInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var connection = config.GetConnectionString("Oracle") ?? Environment.GetEnvironmentVariable("CORPUSLENS_ORACLE_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(connection))
        {
            var options = OracleDatabaseOptions.FromConfiguration(config);
            services.AddDbContext<CorpusDbContext>(o => o.UseOracle(connection));
            services.AddSingleton(options);
            services.AddSingleton<Func<DbConnection>>(() => new OracleConnection(options.ConnectionString));
            services.AddSingleton<OracleCorpusRepository>();
            services.AddSingleton<ISearchRepository, OracleSearchRepository>();
            services.AddSingleton<IRevisionStore, OracleRevisionStore>();
            services.AddSingleton<RevisionSwitchoverService>();
            services.AddSingleton<IKeepAliveProbe, OracleKeepAliveProbe>();
            services.AddSingleton(sp => new OracleKeepAliveHostedService(
                sp.GetRequiredService<IKeepAliveProbe>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<OracleKeepAliveHostedService>>())
            { Interval = TimeSpan.FromHours(Math.Max(1, config.GetValue("Oracle:KeepAliveHours", 24))) });
            services.AddHostedService(sp => sp.GetRequiredService<OracleKeepAliveHostedService>());
        }
        services.AddHttpClient<OpenAiEmbeddingClient>();
        services.AddSingleton<QueryVectorCache>();
        services.AddSingleton<QueryNormalizer>();
        services.AddSingleton<CitationQueryParser>();
        services.AddSingleton<PassageDeduplicator>();
        services.AddSingleton<IReranker, NoOpReranker>();
        services.AddSingleton(new HybridSearchOptions(config.GetValue("Search:VectorWeight", .8), config.GetValue("Search:LexicalWeight", .2), config.GetValue("Search:LayerBoost", .05), config.GetValue("Search:RrfK", 60), config.GetValue("Search:CorroborationBoost", .04)));
        services.AddSingleton(_ => new OpenAiSpendGuard { LedgerPath = Path.Combine(config["Corpus:StatePath"] ?? Path.Combine(NwtEpubCorpusAdapter.FindRepositoryRoot(), "data", "spend"), "openai-queries.json") });
        if (!string.IsNullOrWhiteSpace(connection)) services.AddSingleton<IQueryEmbeddingCacheRepository, OracleQueryEmbeddingCacheRepository>();
        else services.AddSingleton<IQueryEmbeddingCacheRepository, NullQueryEmbeddingCacheRepository>();
        services.AddSingleton<IQueryEmbedder, OpenAiQueryEmbedder>();
        services.AddSingleton<CachedQueryEmbeddingService>();
        if (!string.IsNullOrWhiteSpace(connection)) services.AddSingleton<ISearchCorpus, OracleSearchCorpus>();
        else services.AddSingleton<ISearchCorpus>(_ => new LocalSnapshotSearchCorpus(config["Corpus:SnapshotPath"] ?? Path.Combine(NwtEpubCorpusAdapter.FindRepositoryRoot(), "data", "snapshots")));
        services.AddSingleton<HybridSearchService>();
        services.AddHttpClient<ICorpusSourceAdapter, NwtEpubCorpusAdapter>();
        services.AddSingleton<IChunkingPipeline, InMemoryChunkingPipeline>();
        services.AddSingleton<ICoverageAuditor, CoverageAuditor>();
        return services;
    }
}

public sealed class QueryNormalizer
{
    public string Normalize(string query, string language)
    {
        var normalized = query.Normalize(NormalizationForm.FormC).Trim();
        normalized = string.Join(" ", normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length is 0 or > 500) throw new ArgumentException("Query must contain 1 to 500 characters.");
        return normalized.ToLower(System.Globalization.CultureInfo.GetCultureInfo(language));
    }
    public string CacheKey(string normalized, string language) => Hashes.Sha256($"{CorpusVersions.Normalization}|text-embedding-3-large|1024|{language}|{normalized}");
}

public sealed class QueryVectorCache
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, float[] Vector, DateTimeOffset Expires)>> _items = [];
    private readonly LinkedList<(string Key, float[] Vector, DateTimeOffset Expires)> _lru = new();
    public int Capacity { get; init; } = 256;
    public TimeSpan Ttl { get; init; } = TimeSpan.FromHours(1);
    public bool TryGet(string key, out float[]? vector)
    {
        lock (_gate)
        {
            if (_items.TryGetValue(key, out var node))
            {
                _lru.Remove(node); _items.Remove(key);
                if (node.Value.Expires > DateTimeOffset.UtcNow)
                { _lru.AddFirst(node); _items[key] = node; vector = node.Value.Vector; return true; }
            }
            vector = null; return false;
        }
    }
    public void Put(string key, float[] vector)
    {
        lock (_gate)
        {
            if (_items.TryGetValue(key, out var old)) _lru.Remove(old);
            var node = _lru.AddFirst((key, vector, DateTimeOffset.UtcNow + Ttl));
            _items[key] = node;
            while (_items.Count > Capacity) { var last = _lru.Last!; _items.Remove(last.Value.Key); _lru.RemoveLast(); }
        }
    }
}

public sealed class OpenAiEmbeddingClient(HttpClient http, IConfiguration config)
{
    public const string Model = "text-embedding-3-large";
    public const int Dimensions = 1024;
    private HttpRequestMessage NewRequest(HttpMethod method, string uri)
    {
        var key = config["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? throw new InvalidOperationException("OPENAI_API_KEY is required at runtime.");
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return request;
    }

    public async Task<float[]> EmbedQueryAsync(string query, CancellationToken cancellationToken)
    {
        using var request = NewRequest(HttpMethod.Post, "https://api.openai.com/v1/embeddings");
        request.Content = new StringContent(JsonSerializer.Serialize(new { model = Model, dimensions = Dimensions, input = query }), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.GetProperty("data")[0].GetProperty("embedding").EnumerateArray().Select(v => v.GetSingle()).ToArray();
    }
}

using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CorpusLens.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CorpusLens.Infrastructure;

public static class InfrastructureServices
{
    public static IServiceCollection AddCorpusInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var connection = config.GetConnectionString("Oracle");
        if (!string.IsNullOrWhiteSpace(connection))
            services.AddDbContext<CorpusDbContext>(o => o.UseOracle(connection));
        services.AddHttpClient<OpenAiEmbeddingClient>();
        services.AddHttpClient<IEmbeddingBatchClient, OpenAiEmbeddingClient>();
        services.AddSingleton<QueryVectorCache>();
        services.AddSingleton<QueryNormalizer>();
        services.AddSingleton<ICorpusSourceAdapter, NwtEpubCorpusAdapter>();
        services.AddSingleton<IChunkingPipeline, InMemoryChunkingPipeline>();
        services.AddSingleton<ICoverageAuditor, CoverageAuditor>();
        return services;
    }
}

public sealed class NwtEpubCorpusAdapter : ICorpusSourceAdapter
{
    public Task<IReadOnlyList<SourceArtifact>> DiscoverAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SourceArtifact>>([]);
    public Task<SourceFetchResult> FetchAsync(SourceArtifact artifact, long offset, CancellationToken cancellationToken) => throw new NotSupportedException("EPUB fetching is implemented in Phase 2.");
    public Task<ParseResult> ParseAsync(SourceFetchResult source, CancellationToken cancellationToken) => throw new NotSupportedException("EPUB parsing is implemented in Phase 2.");
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

public sealed class OpenAiEmbeddingClient(HttpClient http, IConfiguration config) : IEmbeddingBatchClient
{
    public const string Model = "text-embedding-3-large";
    public const int Dimensions = 1024;
    public string BuildBatchJsonl(IReadOnlyList<SearchChunk> chunks) => string.Join("\n", chunks.Select(c => JsonSerializer.Serialize(new
    {
        custom_id = c.Id, method = "POST", url = "/v1/embeddings",
        body = new { model = Model, dimensions = Dimensions, input = c.EmbeddingInput }
    }))) + (chunks.Count > 0 ? "\n" : "");

    public async Task<string> SubmitAsync(IReadOnlyList<SearchChunk> chunks, CancellationToken cancellationToken)
    {
        if (chunks.Count == 0) throw new ArgumentException("Batch must contain chunks.");
        using var upload = NewRequest(HttpMethod.Post, "https://api.openai.com/v1/files");
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("batch"), "purpose");
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(BuildBatchJsonl(chunks))), "file", "embeddings.jsonl");
        upload.Content = form;
        using var fileResponse = await http.SendAsync(upload, cancellationToken);
        fileResponse.EnsureSuccessStatusCode();
        using var fileJson = JsonDocument.Parse(await fileResponse.Content.ReadAsStringAsync(cancellationToken));
        var fileId = fileJson.RootElement.GetProperty("id").GetString();
        using var batch = NewRequest(HttpMethod.Post, "https://api.openai.com/v1/batches");
        batch.Content = new StringContent(JsonSerializer.Serialize(new { input_file_id = fileId, endpoint = "/v1/embeddings", completion_window = "24h" }), Encoding.UTF8, "application/json");
        using var batchResponse = await http.SendAsync(batch, cancellationToken);
        batchResponse.EnsureSuccessStatusCode();
        using var batchJson = JsonDocument.Parse(await batchResponse.Content.ReadAsStringAsync(cancellationToken));
        return batchJson.RootElement.GetProperty("id").GetString()!;
    }

    public async Task<string> PollAsync(string batchId, CancellationToken cancellationToken)
    {
        using var request = NewRequest(HttpMethod.Get, $"https://api.openai.com/v1/batches/{Uri.EscapeDataString(batchId)}");
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return json.RootElement.GetProperty("status").GetString()!;
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string uri)
    {
        var key = config["OpenAI:ApiKey"] ?? throw new InvalidOperationException("OpenAI:ApiKey is required at runtime.");
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

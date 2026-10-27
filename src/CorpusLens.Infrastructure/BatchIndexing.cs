using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CorpusLens.Core;

namespace CorpusLens.Infrastructure;

public sealed record BatchShard(string Path, int Requests, long Bytes, long EstimatedTokens, decimal EstimatedCostUsd);

public sealed class BatchJsonlBuilder
{
    public const int MaxRequests = 49_999;
    public const long MaxBytes = 199_000_000;
    public const decimal PricePerMillionTokens = 0.065m;
    public static long EstimateTokens(string input) => Math.Max(1, (Encoding.UTF8.GetByteCount(input) + 3L) / 4L);
    public static decimal Cost(long tokens) => tokens * PricePerMillionTokens / 1_000_000m;
    public IReadOnlyList<BatchShard> Build(string revisionId, IEnumerable<SearchChunk> chunks, string directory, int maxRequests = MaxRequests, long maxBytes = MaxBytes)
    {
        if (maxRequests is < 1 or >= 50_000 || maxBytes is < 1 or >= 200_000_000) throw new ArgumentOutOfRangeException(nameof(maxRequests));
        Directory.CreateDirectory(directory);
        var shards = new List<BatchShard>();
        StreamWriter? writer = null;
        string? path = null;
        var count = 0; long bytes = 0, tokens = 0;
        void Close()
        {
            writer?.Dispose(); writer = null;
            if (path is not null) shards.Add(new BatchShard(path, count, bytes, tokens, Cost(tokens)));
            path = null; count = 0; bytes = 0; tokens = 0;
        }
        foreach (var chunk in chunks.DistinctBy(c => c.Id).OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            if (chunk.RevisionId != revisionId || Hashes.Sha256(chunk.EmbeddingInput) != chunk.InputSha256) throw new InvalidDataException("Invalid chunk revision or checksum.");
            var line = JsonSerializer.Serialize(new { custom_id = revisionId + ":" + chunk.Id, method = "POST", url = "/v1/embeddings",
                body = new { model = "text-embedding-3-large", dimensions = 1024, encoding_format = "float", input = chunk.EmbeddingInput } }) + "\n";
            var lineBytes = Encoding.UTF8.GetByteCount(line);
            if (lineBytes >= maxBytes) throw new InvalidDataException("One Batch request exceeds shard byte limit.");
            if (writer is not null && (count >= maxRequests || bytes + lineBytes >= maxBytes)) Close();
            if (writer is null)
            {
                path = Path.Combine(directory, $"{revisionId[..Math.Min(16, revisionId.Length)]}-{shards.Count:D3}.jsonl");
                writer = new StreamWriter(path, false, new UTF8Encoding(false));
            }
            writer.Write(line);
            count++; bytes += lineBytes; tokens += EstimateTokens(chunk.EmbeddingInput);
        }
        Close();
        return shards;
    }
}

public sealed class BatchOutputImporter
{
    public IReadOnlyList<ChunkEmbedding> Parse(string revisionId, string modelId, IReadOnlyCollection<SearchChunk> chunks, IEnumerable<string> lines)
    {
        var expected = chunks.Select(c => revisionId + ":" + c.Id).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ChunkEmbedding>();
        foreach (var line in lines.Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            var id = root.GetProperty("custom_id").GetString() ?? throw new InvalidDataException("Missing custom_id.");
            if (!expected.Contains(id) || !seen.Add(id)) throw new InvalidDataException("Unexpected or duplicate Batch result ID: " + id);
            if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null) throw new InvalidDataException("Batch result error: " + id);
            var response = root.GetProperty("response");
            if (response.GetProperty("status_code").GetInt32() != 200) throw new InvalidDataException("Failed Batch request: " + id);
            var body = response.GetProperty("body");
            var vector = body.GetProperty("data")[0].GetProperty("embedding").EnumerateArray().Select(v => v.GetSingle()).ToArray();
            if (vector.Length != 1024 || vector.Any(v => !float.IsFinite(v))) throw new InvalidDataException("Corrupt embedding vector: " + id);
            var tokens = body.GetProperty("usage").GetProperty("prompt_tokens").GetInt32();
            result.Add(new ChunkEmbedding(id[(revisionId.Length + 1)..], modelId, vector, tokens));
        }
        if (seen.Count != expected.Count) throw new InvalidDataException($"Missing {expected.Count - seen.Count} Batch results.");
        return result;
    }
}

public sealed class OpenAiBatchEmbeddingService(HttpClient http)
{
    public decimal BudgetUsd { get; init; } = 5m;
    public void CheckBudget(IEnumerable<BatchShard> shards, decimal alreadyCommittedUsd = 0)
    {
        if (alreadyCommittedUsd + shards.Sum(s => s.EstimatedCostUsd) > BudgetUsd) throw new InvalidOperationException("Batch hard budget exceeded.");
    }
    public async Task<string> SubmitAsync(BatchShard shard, string apiKey, CancellationToken token)
    {
        CheckBudget([shard]);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("batch"), "purpose");
        form.Add(new StreamContent(File.OpenRead(shard.Path)), "file", Path.GetFileName(shard.Path));
        using var file = await SendAsync(HttpMethod.Post, "files", apiKey, form, token);
        using var fileJson = JsonDocument.Parse(await file.Content.ReadAsStringAsync(token));
        var fileId = fileJson.RootElement.GetProperty("id").GetString();
        using var body = new StringContent(JsonSerializer.Serialize(new { input_file_id = fileId, endpoint = "/v1/embeddings", completion_window = "24h" }), Encoding.UTF8, "application/json");
        using var batch = await SendAsync(HttpMethod.Post, "batches", apiKey, body, token);
        using var batchJson = JsonDocument.Parse(await batch.Content.ReadAsStringAsync(token));
        return batchJson.RootElement.GetProperty("id").GetString()!;
    }
    public async Task<JsonDocument> StatusAsync(string batchId, string apiKey, CancellationToken token)
    {
        using var response = await SendAsync(HttpMethod.Get, "batches/" + Uri.EscapeDataString(batchId), apiKey, null, token);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
    }
    public async Task<string> DownloadAsync(string fileId, string apiKey, CancellationToken token)
    {
        using var response = await SendAsync(HttpMethod.Get, "files/" + Uri.EscapeDataString(fileId) + "/content", apiKey, null, token);
        return await response.Content.ReadAsStringAsync(token);
    }
    public async Task CancelAsync(string batchId, string apiKey, CancellationToken token)
    {
        using var response = await SendAsync(HttpMethod.Post, "batches/" + Uri.EscapeDataString(batchId) + "/cancel", apiKey, null, token);
    }
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string key, HttpContent? content, CancellationToken token)
    {
        var payload = content is null ? null : await content.ReadAsByteArrayAsync(token);
        var contentType = content?.Headers.ContentType;
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, "https://api.openai.com/v1/" + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            if (payload is not null)
            {
                request.Content = new ByteArrayContent(payload);
                request.Content.Headers.ContentType = contentType;
            }
            var response = await http.SendAsync(request, token);
            if ((response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) && attempt < 5)
            { response.Dispose(); await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), token); continue; }
            response.EnsureSuccessStatusCode();
            return response;
        }
    }
}

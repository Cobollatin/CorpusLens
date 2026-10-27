using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CorpusLens.Core;
using CorpusLens.Infrastructure;
using CorpusLens.Infrastructure.Persistence;
using Oracle.ManagedDataAccess.Client;

namespace CorpusLens.Worker;

public static class DirectBackfill
{
    private const decimal PricePerMillion = .13m;
    private const decimal HardCap = 5m;
    // Reserve the full planning estimate for the four original and four replacement batches.
    private const decimal PriorBatchReserve = 2m;
    public static async Task RunAsync(string language, string root, string snapshotDirectory)
    {
        if (language is not ("E" or "S") || Environment.GetEnvironmentVariable("CORPUSLENS_ALLOW_ORACLE_CONNECT") != "yes")
            throw new ArgumentException("Use direct-backfill --lang E|S with authorized Oracle access.");
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("OPENAI_API_KEY is required.");
        var snapshot = await CorpusSnapshotSerializer.ReadAsync(Path.Combine(snapshotDirectory, $"nwt_{language}.snapshot.json"));
        var revision = snapshot.Audit.RevisionId;
        var directory = Path.Combine(root, "data", "batch");
        Directory.CreateDirectory(directory);
        var ledgerPath = Path.Combine(directory, "direct-spend.json");
        var ledger = File.Exists(ledgerPath) ? JsonSerializer.Deserialize<DirectSpend>(await File.ReadAllTextAsync(ledgerPath))! : new DirectSpend(0, 0);
        var options = OracleDatabaseOptions.FromEnvironment();
        var existing = new HashSet<string>(StringComparer.Ordinal);
        await using (var connection = new OracleConnection(options.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.BindByName = true;
            command.CommandText = "SELECT e.chunk_id FROM chunk_embedding e JOIN search_chunk c ON c.id=e.chunk_id WHERE c.revision_id=:revision";
            command.Parameters.Add("revision", OracleDbType.Varchar2).Value = revision;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) existing.Add(reader.GetString(0));
        }
        var remaining = snapshot.Chunks.DistinctBy(c => c.Id).Where(c => !existing.Contains(c.Id)).ToArray();
        Console.WriteLine($"{language}: {existing.Count} existing, {remaining.Length} remaining");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var repository = new OracleCorpusRepository(() => new OracleConnection(options.ConnectionString));
        foreach (var group in remaining.Chunk(500))
        {
            var estimatedTokens = group.Sum(c => (Encoding.UTF8.GetByteCount(c.EmbeddingInput) + 2L) / 3L + 10L);
            var reservation = estimatedTokens * PricePerMillion / 1_000_000m;
            if (PriorBatchReserve + ledger.Tokens * PricePerMillion / 1_000_000m + reservation > HardCap)
                throw new InvalidOperationException("Direct embedding hard spend cap reached.");
            using var requestBody = new StringContent(JsonSerializer.Serialize(new { model = "text-embedding-3-large", dimensions = 1024, input = group.Select(c => c.EmbeddingInput).ToArray() }), Encoding.UTF8, "application/json");
            var payload = await requestBody.ReadAsByteArrayAsync();
            JsonDocument? result = null;
            for (var attempt = 0; attempt < 8; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/embeddings");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                request.Content = new ByteArrayContent(payload);
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                using var response = await http.SendAsync(request);
                if ((response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden || (int)response.StatusCode >= 500) && attempt < 7)
                {
                    Console.WriteLine($"{language}: embedding HTTP {(int)response.StatusCode}; retry {attempt + 1}/7");
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt))));
                    continue;
                }
                response.EnsureSuccessStatusCode();
                result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                break;
            }
            using (result)
            {
                if (result is null) throw new InvalidOperationException("Embedding response unavailable.");
                var rootElement = result.RootElement;
                var tokens = rootElement.GetProperty("usage").GetProperty("prompt_tokens").GetInt64();
                if (PriorBatchReserve + (ledger.Tokens + tokens) * PricePerMillion / 1_000_000m > HardCap)
                    throw new InvalidOperationException("Direct embedding hard spend cap reached after provider usage.");
                var vectors = new ChunkEmbedding[group.Length];
                var seen = new HashSet<int>();
                foreach (var item in rootElement.GetProperty("data").EnumerateArray())
                {
                    var index = item.GetProperty("index").GetInt32();
                    if (index < 0 || index >= group.Length || !seen.Add(index)) throw new InvalidDataException("Unexpected embedding index.");
                    var vector = item.GetProperty("embedding").EnumerateArray().Select(v => v.GetSingle()).ToArray();
                    if (vector.Length != 1024 || vector.Any(v => !float.IsFinite(v))) throw new InvalidDataException("Invalid embedding vector.");
                    vectors[index] = new ChunkEmbedding(group[index].Id, "text-embedding-3-large-1024", vector, (int)(tokens / group.Length));
                }
                if (seen.Count != group.Length) throw new InvalidDataException("Missing direct embeddings.");
                await repository.BulkInsertEmbeddingsAsync("text-embedding-3-large-1024", vectors, CancellationToken.None);
                ledger = ledger with { Tokens = ledger.Tokens + tokens, Vectors = ledger.Vectors + group.Length };
                await File.WriteAllTextAsync(ledgerPath, JsonSerializer.Serialize(ledger));
                Console.WriteLine($"{language}: {ledger.Vectors} direct vectors, {ledger.Tokens} direct tokens, ${ledger.Tokens * PricePerMillion / 1_000_000m:F4} direct cost");
            }
        }
    }
    private sealed record DirectSpend(long Tokens, long Vectors);
}

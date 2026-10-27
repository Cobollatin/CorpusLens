using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CorpusLens.Core;
using Microsoft.Extensions.Configuration;

namespace CorpusLens.Infrastructure;

public interface ISearchCorpus
{
    IReadOnlyDictionary<string, Verse> Verses { get; }
    IReadOnlyList<Pericope> Pericopes { get; }
    string CoverageStatus { get; }
    bool EmbeddingsPopulated { get; }
    IReadOnlyList<SearchCandidate> Retrieve(string query, string language, string? edition, float[]? vector, string mode);
}

public sealed class LocalSnapshotSearchCorpus : ISearchCorpus
{
    private sealed record Doc(string Id, string Layer, string Language, string[] VerseIds, int Length, Dictionary<string, int> Terms);
    private readonly Dictionary<string, Verse> _verses = [];
    private readonly List<Pericope> _pericopes = [];
    private readonly Dictionary<string, List<Doc>> _postings = [];
    private readonly Dictionary<string, List<Doc>> _documents = [];
    private readonly IReadOnlyDictionary<string, float[]> _vectors;
    private readonly Dictionary<string, int> _counts = [];
    private readonly Dictionary<string, int> _lengths = [];
    public IReadOnlyDictionary<string, Verse> Verses => _verses;
    public IReadOnlyList<Pericope> Pericopes => _pericopes;
    public string CoverageStatus { get; private set; } = "No local snapshots loaded";
    public bool EmbeddingsPopulated => _vectors.Count > 0;
    public LocalSnapshotSearchCorpus(string snapshotDirectory, IReadOnlyDictionary<string, float[]>? vectors = null)
    {
        _vectors = vectors ?? new Dictionary<string, float[]>();
        foreach (var lang in new[] { "E", "S" })
        {
            var path = Path.Combine(snapshotDirectory, $"nwt_{lang}.snapshot.json");
            if (!File.Exists(path)) continue;
            var snapshot = CorpusSnapshotSerializer.ReadAsync(path).GetAwaiter().GetResult();
            if (!snapshot.Audit.Passed) throw new InvalidDataException($"Snapshot {lang} failed coverage audit.");
            foreach (var verse in snapshot.Verses) _verses[lang + ":" + verse.SpanId] = verse;
            _pericopes.AddRange(snapshot.Pericopes);
            var spans = snapshot.Verses.ToDictionary(v => v.SpanId);
            foreach (var chunk in snapshot.Chunks)
            {
                var ids = chunk.Segments.Select(s => s.TextSpanId).Distinct().ToArray();
                var tokens = Tokenize(string.Join(" ", ids.Select(id => spans[id].Text)));
                var terms = tokens.GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
                var doc = new Doc(chunk.Id, chunk.Layer, lang, ids, tokens.Length, terms);
                if (!_documents.TryGetValue(lang, out var documents)) _documents[lang] = documents = [];
                documents.Add(doc);
                foreach (var term in terms.Keys)
                {
                    var key = lang + ":" + term;
                    if (!_postings.TryGetValue(key, out var posting)) _postings[key] = posting = [];
                    posting.Add(doc);
                }
                _counts[lang] = _counts.GetValueOrDefault(lang) + 1;
                _lengths[lang] = _lengths.GetValueOrDefault(lang) + tokens.Length;
            }
        }
        CoverageStatus = _counts.Count == 2 ? "Audited EN and ES snapshots loaded" : $"{_counts.Count}/2 audited snapshots loaded";
    }
    public static string[] Tokenize(string text) => Regex.Matches(Fold(text), @"[\p{L}\p{N}]+").Select(m => m.Value).Where(t => t.Length > 1).ToArray();
    private static string Fold(string value) => new string(value.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant();
    public IReadOnlyList<SearchCandidate> Retrieve(string query, string language, string? edition, float[]? vector, string mode)
    {
        if (edition is not null && edition != "New World Translation") return [];
        var results = new Dictionary<string, (Doc Doc, double Score)>();
        foreach (var lang in language == "All" ? new[] { "E", "S" } : new[] { language })
        {
            if (!_counts.TryGetValue(lang, out var count)) continue;
            var avg = (double)_lengths[lang] / count;
            var queryTerms = LexicalQuery.Terms(query, lang);
            foreach (var token in queryTerms)
            {
                if (!_postings.TryGetValue(lang + ":" + token, out var posting)) continue;
                var idf = Math.Log(1 + (count - posting.Count + .5) / (posting.Count + .5));
                foreach (var doc in posting)
                {
                    var tf = doc.Terms[token];
                    var score = idf * tf * 2.2 / (tf + 1.2 * (.25 + .75 * doc.Length / avg));
                    var key = lang + ":" + doc.Id;
                    results[key] = (doc, results.GetValueOrDefault(key).Score + score);
                }
            }
        }
        var termsByLanguage = (language == "All" ? new[] { "E", "S" } : new[] { language })
            .ToDictionary(lang => lang, lang => LexicalQuery.Terms(query, lang));
        var lexical = mode == "Semantic" ? [] : results.Values
            .Select(x => (x.Doc, Score: x.Score * LexicalQuery.CoverageWeight(termsByLanguage[x.Doc.Language], x.Doc.Terms)))
            .OrderByDescending(x => x.Score).Take(150)
            .Select((x, i) => new SearchCandidate(x.Doc.Id, x.Doc.Layer, x.Doc.VerseIds, null, x.Score, null, i + 1, x.Doc.Language)).ToArray();
        if (vector is null || mode == "Lexical") return lexical;
        var dense = (language == "All" ? new[] { "E", "S" } : new[] { language }).Where(_documents.ContainsKey).SelectMany(lang => _documents[lang])
            .Where(doc => _vectors.ContainsKey(doc.Id)).Select(doc => (Doc: doc, Score: Cosine(vector, _vectors[doc.Id])))
            .OrderByDescending(x => x.Score).Take(150).ToArray();
        var merged = lexical.ToDictionary(x => x.ChunkId);
        for (var i = 0; i < dense.Length; i++)
        {
            var x = dense[i];
            merged[x.Doc.Id] = merged.TryGetValue(x.Doc.Id, out var prior)
                ? prior with { DenseScore = x.Score, DenseRank = i + 1 }
                : new SearchCandidate(x.Doc.Id, x.Doc.Layer, x.Doc.VerseIds, x.Score, null, i + 1, null, x.Doc.Language);
        }
        return merged.Values.ToArray();
    }
    private static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length) return double.NegativeInfinity;
        double dot = 0, aa = 0, bb = 0;
        for (var i = 0; i < a.Length; i++) { dot += a[i] * b[i]; aa += a[i] * a[i]; bb += b[i] * b[i]; }
        return aa == 0 || bb == 0 ? double.NegativeInfinity : dot / Math.Sqrt(aa * bb);
    }
}

public sealed record QueryEmbeddingResult(float[]? Vector, string CacheState, bool Degraded);
public interface IQueryEmbeddingCacheRepository
{
    Task<float[]?> GetAsync(string key, CancellationToken token);
    Task PutAsync(string key, string language, float[] vector, CancellationToken token);
}
public sealed class NullQueryEmbeddingCacheRepository : IQueryEmbeddingCacheRepository
{
    public Task<float[]?> GetAsync(string key, CancellationToken token) => Task.FromResult<float[]?>(null);
    public Task PutAsync(string key, string language, float[] vector, CancellationToken token) => Task.CompletedTask;
}
public interface IQueryEmbedder { Task<float[]> EmbedAsync(string query, CancellationToken token); }
public sealed class OpenAiQueryEmbedder(OpenAiEmbeddingClient client) : IQueryEmbedder
{
    public Task<float[]> EmbedAsync(string query, CancellationToken token) => client.EmbedQueryAsync(query, token);
}
public sealed class OpenAiSpendGuard
{
    private readonly object _gate = new();
    private readonly Dictionary<string, decimal> _daily = [], _monthly = [];
    public decimal DailyCap { get; init; } = .50m;
    public decimal MonthlyCap { get; init; } = 2m;
    public string? LedgerPath { get; init; }
    public bool TryReserve(decimal estimatedUsd, DateTimeOffset now)
    {
        var day = now.UtcDateTime.ToString("yyyy-MM-dd"); var month = now.UtcDateTime.ToString("yyyy-MM");
        lock (_gate)
        {
            if (LedgerPath is null) return Reserve(_daily, _monthly, day, month, estimatedUsd);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(LedgerPath))!);
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    using var stream = new FileStream(LedgerPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    SpendLedger ledger;
                    try { ledger = stream.Length == 0 ? new([], []) : System.Text.Json.JsonSerializer.Deserialize<SpendLedger>(stream) ?? new([], []); }
                    catch (System.Text.Json.JsonException) { throw new InvalidDataException("Spend ledger is corrupt; provider access denied."); }
                    if (!Reserve(ledger.Daily, ledger.Monthly, day, month, estimatedUsd)) return false;
                    stream.SetLength(0); stream.Position = 0;
                    System.Text.Json.JsonSerializer.Serialize(stream, ledger);
                    stream.Flush(true);
                    return true;
                }
                catch (IOException) when (attempt < 19) { Thread.Sleep(25); }
            }
            return false;
        }
    }
    private bool Reserve(Dictionary<string, decimal> daily, Dictionary<string, decimal> monthly, string day, string month, decimal usd)
    {
        if (daily.GetValueOrDefault(day) + usd > DailyCap || monthly.GetValueOrDefault(month) + usd > MonthlyCap) return false;
        daily[day] = daily.GetValueOrDefault(day) + usd; monthly[month] = monthly.GetValueOrDefault(month) + usd;
        return true;
    }
    private sealed record SpendLedger(Dictionary<string, decimal> Daily, Dictionary<string, decimal> Monthly);
}
public sealed class CachedQueryEmbeddingService(QueryNormalizer normalizer, QueryVectorCache memory, IQueryEmbeddingCacheRepository database, IQueryEmbedder embedder, OpenAiSpendGuard guard, IConfiguration config)
{
    public async Task<QueryEmbeddingResult> GetAsync(string query, string language, CancellationToken token)
    {
        var key = normalizer.CacheKey(query, language);
        if (memory.TryGet(key, out var l1)) return new(l1, "L1 hit", false);
        var l2 = await database.GetAsync(key, token);
        if (l2 is not null) { memory.Put(key, l2); return new(l2, "L2 hit", false); }
        if (!config.GetValue("OpenAI:EnableLiveEmbeddings", false) || string.IsNullOrWhiteSpace(config["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY"))) return new(null, "embeddings unavailable", true);
        var estimate = Math.Max(1, query.Length) * .13m / 1_000_000m;
        bool reserved;
        try { reserved = guard.TryReserve(estimate, DateTimeOffset.UtcNow); }
        catch (IOException) { reserved = false; }
        if (!reserved) return new(null, "spend guard reached", true);
        try
        {
            var vector = await embedder.EmbedAsync(query, token);
            if (vector.Length != 1024 || vector.Any(v => !float.IsFinite(v))) throw new InvalidDataException("Invalid query embedding.");
            memory.Put(key, vector); await database.PutAsync(key, language, vector, token);
            return new(vector, "cache miss", false);
        }
        catch (Exception) when (!token.IsCancellationRequested) { return new(null, "embeddings unavailable", true); }
    }
}

public sealed class HybridSearchService(ISearchCorpus corpus, QueryNormalizer normalizer, CitationQueryParser citations, CachedQueryEmbeddingService embeddings, IReranker reranker, PassageDeduplicator deduplicator, HybridSearchOptions options)
{
    public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken token = default)
    {
        var lang = request.Language is "E" or "S" ? request.Language : throw new ArgumentException("Language must be E or S.");
        var query = normalizer.Normalize(request.Query, lang == "S" ? "es" : "en");
        var citation = citations.Parse(query);
        var mode = request is SearchQuery q && q.Mode is "Lexical" or "Semantic" or "Hybrid" ? q.Mode : "Hybrid";
        QueryEmbeddingResult embedding = mode == "Lexical" ? new(null, "lexical mode", false)
            : !corpus.EmbeddingsPopulated ? new(null, "Phase 6 backfill pending", true)
            : await embeddings.GetAsync(query, lang, token);
        var candidates = corpus.Retrieve(query, lang, request.Edition, embedding.Vector, embedding.Degraded ? "Lexical" : mode);
        if (citation is not null && (corpus is not LocalSnapshotSearchCorpus || request.Edition is null or "New World Translation"))
        {
            var ids = citation.VerseIds.Where(id => corpus.Verses.ContainsKey(lang + ":" + id)).ToArray();
            if (ids.Length > 0) candidates = [new SearchCandidate("citation:" + lang + ids[0], "SingleVerse", ids, null, 1, null, 1, lang), .. candidates];
        }
        var reranked = await reranker.RerankAsync(query, candidates.Take(200).ToArray(), token);
        var results = new[] { lang }.SelectMany(selectedLanguage =>
        {
            var verseMap = corpus.Verses.Where(k => k.Key.StartsWith(selectedLanguage + ":")).ToDictionary(k => k.Key[2..], v => v.Value);
            return deduplicator.Build(reranked.Where(c => c.Language == selectedLanguage).ToArray(), verseMap, corpus.Pericopes, selectedLanguage, options, citation, Math.Clamp(request.Limit, 1, 50));
        }).OrderByDescending(r => r.Score).Take(Math.Clamp(request.Limit, 1, 50)).ToArray();
        return new(results, citation, embedding.Degraded, embedding.CacheState, corpus.CoverageStatus, mode);
    }
}
public record SearchQuery(string Query, string Language, string? Edition, int Limit = 10, string Mode = "Hybrid") : SearchRequest(Query, Language, Edition, Limit);

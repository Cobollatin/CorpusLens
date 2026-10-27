using System.Net;
using System.Net.Http.Json;
using CorpusLens.Core;
using CorpusLens.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CorpusLens.UnitTests;

public class Phase4Tests
{
    [Theory]
    [InlineData("John 3:16", "43003016", "43003016")]
    [InlineData("Juan 3:16", "43003016", "43003016")]
    [InlineData("Mateo 6:25-34", "40006025", "40006034")]
    [InlineData("Gen 1:1", "01001001", "01001001")]
    [InlineData("Salmos 23:1-4", "19023001", "19023004")]
    [InlineData("Romans 8:31-39", "45008031", "45008039")]
    public void CitationParsing(string query, string first, string last)
    {
        var ids = new CitationQueryParser().Parse(query)!.VerseIds;
        Assert.Equal(first, ids[0]); Assert.Equal(last, ids[^1]);
    }
    [Fact] public void RrfUsesWeightsAndK()
    {
        var score = ReciprocalRankFusion.Score(new SearchCandidate("a", "verse", ["43003016"], null, null, 2, 1), new HybridSearchOptions(2, 1, 0, 60));
        Assert.Equal(2.0 / 62 + 1.0 / 61, score.Total, 10);
    }
    [Fact] public void DeduplicatorCoalescesAndHighlightsAnchor()
    {
        var verses = Enumerable.Range(16, 3).ToDictionary(n => BibleVerseId.Format(43, 3, n), n => new Verse(43, 3, n, $"Verse {n}", "John", BibleVerseId.Format(43, 3, n)));
        var hits = new[] { new SearchCandidate("a", "SlidingWindow3Step1", ["43003016", "43003017"], null, 1, null, 1), new SearchCandidate("b", "SingleVerse", ["43003017"], null, 2, null, 2) };
        var result = new PassageDeduplicator().Build(hits, verses, [], "E", new(), null, 10);
        Assert.Single(result); Assert.Equal(2, result[0].Verses.Count); Assert.Equal(17, Assert.Single(result[0].Verses.Where(v => v.IsPrimaryHighlight)).Number);
    }
    [Fact] public void OverlappingChunksDoNotMultiplyPassageScore()
    {
        var id = BibleVerseId.Format(40, 6, 25);
        var verses = new Dictionary<string, Verse> { [id] = new Verse(40, 6, 25, "Do not be anxious", "Matthew", id) };
        var hit = new SearchCandidate("a", "SingleVerse", [id], null, null, 1, null);
        var result = new PassageDeduplicator().Build([hit, hit with { ChunkId = "b" }], verses, [], "E", new(), null, 10);
        Assert.Single(result);
        Assert.Equal(ReciprocalRankFusion.Score(hit, new()).Total, result[0].Score, 10);
    }
    [Fact] public void CacheKeyUsesNormalizationAndLanguage()
    {
        var normalizer = new QueryNormalizer();
        Assert.Equal(normalizer.CacheKey(normalizer.Normalize("  John   3:16 ", "en"), "E"), normalizer.CacheKey(normalizer.Normalize("john 3:16", "en"), "E"));
        Assert.NotEqual(normalizer.CacheKey("john 3:16", "E"), normalizer.CacheKey("john 3:16", "S"));
    }
    [Fact] public async Task L1AndL2HitsAvoidEmbedder()
    {
        var normalizer = new QueryNormalizer(); var key = normalizer.CacheKey("hello", "E");
        var db = new FakeCache { Value = [1f] }; var embedder = new FakeEmbedder();
        var service = new CachedQueryEmbeddingService(normalizer, new QueryVectorCache(), db, embedder, new(), Config());
        Assert.Equal("L2 hit", (await service.GetAsync("hello", "E", default)).CacheState);
        Assert.Equal("L1 hit", (await service.GetAsync("hello", "E", default)).CacheState);
        Assert.Equal(1, db.Reads); Assert.Equal(0, embedder.Calls);
    }
    [Fact] public async Task SpendGuardDegradesToLexical()
    {
        var service = new CachedQueryEmbeddingService(new QueryNormalizer(), new QueryVectorCache(), new FakeCache(), new FakeEmbedder(), new OpenAiSpendGuard { DailyCap = 0 }, Config());
        var result = await service.GetAsync("hello", "E", default);
        Assert.True(result.Degraded); Assert.Null(result.Vector); Assert.Equal("spend guard reached", result.CacheState);
    }
    [Fact] public void SpendLedgerSurvivesNewGuardInstance()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var now = new DateTimeOffset(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);
            Assert.True(new OpenAiSpendGuard { LedgerPath = path, DailyCap = .5m, MonthlyCap = 2m }.TryReserve(.4m, now));
            Assert.False(new OpenAiSpendGuard { LedgerPath = path, DailyCap = .5m, MonthlyCap = 2m }.TryReserve(.2m, now));
        }
        finally { File.Delete(path); }
    }
    [Fact] public void OracleTextQueryEscapesOperators()
    {
        Assert.Equal("{love} ACCUM {peace}", CorpusLens.Infrastructure.Persistence.OracleSearchRepository.EscapeTextQuery("love & peace"));
    }
    [Theory]
    [InlineData("cómo lidiar con la ansiedad por el dinero", "S", "{lidiar} ACCUM {ansiedad} ACCUM {dinero}")]
    [InlineData("how to deal with anxiety about money", "E", "{deal} ACCUM {anxiety} ACCUM {money}")]
    [InlineData("Juan 3:16", "S", "{juan} ACCUM {3} ACCUM {16}")]
    public void LexicalQueriesRemoveStopwords(string query, string language, string expected)
    {
        Assert.Equal(expected, LexicalQuery.OracleText(query, language));
    }
    [Fact] public void SingleContentTermMatchHasReducedLexicalWeight()
    {
        var terms = LexicalQuery.Terms("cómo lidiar con la ansiedad por el dinero", "S");
        Assert.Equal(.25, LexicalQuery.CoverageWeight(terms, new Dictionary<string, int> { ["dinero"] = 3 }));
        Assert.Equal(1, LexicalQuery.CoverageWeight(terms, new Dictionary<string, int> { ["ansiedad"] = 1, ["dinero"] = 1 }));
    }
    [Fact] public void VectorWeightOutweighsSingleLexicalHit()
    {
        var options = new HybridSearchOptions();
        var vector = ReciprocalRankFusion.Score(new SearchCandidate("v", "SingleVerse", ["40006025"], null, null, 10, null), options);
        var lexical = ReciprocalRankFusion.Score(new SearchCandidate("l", "SingleVerse", ["20015016"], null, null, null, 1), options);
        Assert.True(vector.Total > lexical.Total);
    }
    [Fact] public void CitationAlwaysRanksFirst()
    {
        var ids = new[] { "43003016", "43003017" };
        var verses = ids.ToDictionary(id => id, id => new Verse(43, 3, int.Parse(id[5..]), "text", "John", id));
        var hits = new[]
        {
            new SearchCandidate("unrelated", "SingleVerse", [ids[1]], null, null, 1, 1),
            new SearchCandidate("citation", "SingleVerse", [ids[0]], null, 1, null, 1)
        };
        var result = new PassageDeduplicator().Build(hits, verses, [], "E", new(), new Citation(43, 3, 16, 16, "john"), 2);
        Assert.Equal(ids[0], result[0].Verses[0].Id);
    }
    [Fact] public async Task ApiEndpoints()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ISearchCorpus>(); services.AddSingleton<ISearchCorpus, FakeCorpus>();
        }));
        using var client = factory.CreateClient();
        var health = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/health");
        Assert.False(health.GetProperty("embeddingsPopulated").GetBoolean());
        Assert.True(health.GetProperty("phase6BackfillPending").GetBoolean());
        var corpora = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/corpora");
        Assert.True(corpora[0].GetProperty("phase6BackfillPending").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/citations?query=John%203%3A16")).StatusCode);
        var chapter = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/chapters/E/43/3");
        Assert.Equal("43003016", chapter.GetProperty("verses")[0].GetProperty("id").GetString());
        Assert.Contains("jw.org", chapter.GetProperty("verses")[0].GetProperty("jwLink").GetString());
        var response = await client.PostAsJsonAsync("/api/v1/search", new SearchQuery("John 3:16", "E", null, 10, "Hybrid"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.True(body.GetProperty("degradedToLexicalOnly").GetBoolean());
        Assert.Contains("43003016", body.ToString());
        var all = await client.PostAsJsonAsync("/api/v1/search", new SearchQuery("John 3:16", "All", null));
        Assert.Equal(HttpStatusCode.BadRequest, all.StatusCode);
        var missing = await client.PostAsJsonAsync("/api/v1/search", new { query = "John 3:16", limit = 10 });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
    }
    private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["OpenAI:EnableLiveEmbeddings"] = "true", ["OpenAI:ApiKey"] = "test-not-sent" }).Build();
    private sealed class FakeCache : IQueryEmbeddingCacheRepository
    {
        public float[]? Value; public int Reads;
        public Task<float[]?> GetAsync(string key, CancellationToken token) { Reads++; return Task.FromResult(Value); }
        public Task PutAsync(string key, string language, float[] vector, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class FakeEmbedder : IQueryEmbedder
    {
        public int Calls;
        public Task<float[]> EmbedAsync(string query, CancellationToken token) { Calls++; return Task.FromResult(new float[1024]); }
    }
    private sealed class FakeCorpus : ISearchCorpus
    {
        public IReadOnlyDictionary<string, Verse> Verses { get; } = new Dictionary<string, Verse> { ["E:43003016"] = new(43, 3, 16, "For God loved the world", "John", "43003016") };
        public IReadOnlyList<Pericope> Pericopes => [];
        public string CoverageStatus => "fixture";
        public bool EmbeddingsPopulated => false;
        public IReadOnlyList<SearchCandidate> Retrieve(string query, string language, string? edition, float[]? vector, string mode) => [new("fixture", "verse", ["43003016"], null, 1, null, 1)];
    }
}

using System.Text.Json;
using CorpusLens.Core;
using CorpusLens.Infrastructure;
using CorpusLens.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace CorpusLens.UnitTests;

public class Phase3Tests
{
    private static Verse Verse(int n) => new(1, 1, n, $"verse {n}", "Genesis", $"s{n}");

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 1, 1)]
    [InlineData(3, 1, 1)]
    [InlineData(4, 2, 1)]
    [InlineData(5, 3, 1)]
    [InlineData(6, 4, 2)]
    [InlineData(7, 5, 2)]
    [InlineData(8, 6, 3)]
    public void WindowsAreFullAndCoverAllVerses(int length, int threes, int fives)
    {
        var chunks = new InMemoryChunkingPipeline().Build("r", Enumerable.Range(1, length).Select(Verse).ToArray(), []);
        var three = chunks.Where(c => c.Layer == "SlidingWindow3Step1").ToArray();
        var five = chunks.Where(c => c.Layer == "SlidingWindow5Step2").ToArray();
        Assert.Equal(threes, three.Length); Assert.Equal(fives, five.Length);
        Assert.All(three, c => Assert.Equal(Math.Min(3, length), c.Segments.Count));
        Assert.All(five, c => Assert.Equal(Math.Min(5, length), c.Segments.Count));
        Assert.Equal(length, five.SelectMany(c => c.Segments).Select(s => s.TextSpanId).Distinct().Count());
        Assert.Equal($"s{length}", five[^1].Segments[^1].TextSpanId);
    }

    [Fact]
    public void ShardsSplitBeforeFiftyThousandRequests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "corpuslens-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var chunks = Enumerable.Range(0, 50_001).Select(i => new SearchChunk(i.ToString("D6"), "r", "SingleVerse", "a", Hashes.Sha256("a"), [])).ToArray();
            var shards = new BatchJsonlBuilder().Build("r", chunks.Append(chunks[0]), dir);
            Assert.Equal(2, shards.Count);
            Assert.Equal(49_999, shards[0].Requests);
            Assert.Equal(2, shards[1].Requests);
            Assert.Equal(50_001, shards.Sum(s => s.Requests));
            Assert.All(shards, s => Assert.True(s.Bytes < 200_000_000));
            using var first = JsonDocument.Parse(File.ReadLines(shards[0].Path).First());
            Assert.Equal("r:000000", first.RootElement.GetProperty("custom_id").GetString());
            Assert.Equal("float", first.RootElement.GetProperty("body").GetProperty("encoding_format").GetString());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void BudgetGuardRefusesOverCap()
    {
        var service = new OpenAiBatchEmbeddingService(new HttpClient());
        var shard = new BatchShard("none", 1, 10, 100, 4.9m);
        service.CheckBudget([shard]);
        Assert.Throws<InvalidOperationException>(() => service.CheckBudget([shard], 0.2m));
    }

    [Fact]
    public void ImportRequiresAllFinite1024Vectors()
    {
        var chunk = new SearchChunk("c", "r", "SingleVerse", "text", Hashes.Sha256("text"), []);
        var importer = new BatchOutputImporter();
        string Line(float[] vector) => JsonSerializer.Serialize(new { custom_id = "r:c", response = new { status_code = 200,
            body = new { data = new[] { new { embedding = vector } }, usage = new { prompt_tokens = 3 } } }, error = (object?)null });
        Assert.Throws<InvalidDataException>(() => importer.Parse("r", "m", [chunk], []));
        Assert.Throws<InvalidDataException>(() => importer.Parse("r", "m", [chunk], [Line([1])]));
        var invalid = Line(new float[1024]).Replace("[0,0", "[1e100,0", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => importer.Parse("r", "m", [chunk], [invalid]));
        Assert.Single(importer.Parse("r", "m", [chunk], [Line(new float[1024])]));
    }

    [Fact]
    public async Task SwitchoverPreservesOldActiveOnFailedAudits()
    {
        var store = new FakeRevisionStore(); var service = new RevisionSwitchoverService(store);
        var good = new CoverageAuditReport("new", true, [], []);
        Assert.False(await service.ActivateAsync("new", good, new EmbeddingCompletenessAudit(false, 4, 3)));
        Assert.Equal("old", store.Active);
        Assert.Equal("new", store.Quarantined);
        Assert.True(await service.ActivateAsync("new", good, new EmbeddingCompletenessAudit(true, 4, 4)));
        Assert.Equal("new", store.Active);
    }
    private sealed class FakeRevisionStore : IRevisionStore
    {
        public string Active = "old"; public string? Quarantined;
        public Task SwitchAtomicallyAsync(string id, CancellationToken token) { Active = id; return Task.CompletedTask; }
        public Task QuarantineAsync(string id, CancellationToken token) { Quarantined = id; return Task.CompletedTask; }
    }

    [Fact]
    public async Task EmbeddedDdlContainsAllTablesAndBothIndexOptions()
    {
        var schema = OracleSchemaRunner.ReadEmbedded("001_schema.sql");
        Assert.Equal(14, OracleSchemaRunner.Statements(schema).Count);
        Assert.Contains("VECTOR(1024, FLOAT32)", schema);
        Assert.Contains("WORLD_LEXER", OracleSchemaRunner.ReadEmbedded("002_text.sql"));
        Assert.Contains("ORGANIZATION INMEMORY NEIGHBOR GRAPH", OracleSchemaRunner.ReadEmbedded("003_hnsw.sql"));
        Assert.Contains("ORGANIZATION NEIGHBOR PARTITIONS", OracleSchemaRunner.ReadEmbedded("003_ivf_fallback.sql"));
        var executed = new List<string>();
        await OracleSchemaRunner.RunWithExecutorAsync(sql => { executed.Add(sql); return Task.CompletedTask; }, useIvf: true);
        Assert.Equal(17, executed.Count);
        Assert.Contains("ORGANIZATION NEIGHBOR PARTITIONS", executed[^1]);
    }

    [Fact]
    public async Task KeepAliveRecordsSuccessAndRetriesFailures()
    {
        var probe = new FakeProbe(); var service = new OracleKeepAliveHostedService(probe, NullLogger<OracleKeepAliveHostedService>.Instance);
        Assert.False(await service.ProbeOnceAsync()); Assert.Null(service.LastHeartbeatUtc);
        Assert.True(await service.ProbeOnceAsync()); Assert.NotNull(service.LastHeartbeatUtc);
        Assert.NotNull(service.LastLatency);
    }
    private sealed class FakeProbe : IKeepAliveProbe
    {
        private int _calls;
        public Task ProbeAsync(CancellationToken token) { if (++_calls == 1) throw new InvalidOperationException("offline"); return Task.CompletedTask; }
    }
}

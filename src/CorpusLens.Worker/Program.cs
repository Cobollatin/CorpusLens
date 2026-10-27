using CorpusLens.Infrastructure;
using CorpusLens.Worker;
using CorpusLens.Core;
using System.Security.Cryptography;
using System.Text.Json;
using CorpusLens.Infrastructure.Persistence;
using Oracle.ManagedDataAccess.Client;

if (args.Length > 0 && args[0] is "schema-migrate" or "revision-stage" or "revision-activate")
{
    if (Environment.GetEnvironmentVariable("CORPUSLENS_ALLOW_ORACLE_CONNECT") != "yes")
        throw new InvalidOperationException("Set CORPUSLENS_ALLOW_ORACLE_CONNECT=yes for an authorized Oracle connection.");
    var options = OracleDatabaseOptions.FromEnvironment();
    if (args[0] == "schema-migrate")
    {
        if (args.Length > 2 || (args.Length == 2 && args[1] != "--ivf")) throw new ArgumentException("Use schema-migrate [--ivf].");
        await new OracleSchemaRunner(() => new OracleConnection(options.ConnectionString)).RunAsync(args.Length == 2);
    }
    else
    {
        if (args.Length != 3 || args[1] != "--lang" || args[2] is not ("E" or "S")) throw new ArgumentException("Use revision-stage --lang E|S.");
        var root = NwtEpubCorpusAdapter.FindRepositoryRoot();
        var snapshot = await CorpusSnapshotSerializer.ReadAsync(Path.Combine(SnapshotDirectory(root), $"nwt_{args[2]}.snapshot.json"));
        if (args[0] == "revision-stage")
            await new OracleCorpusRepository(() => new OracleConnection(options.ConnectionString)).StageSnapshotAsync(snapshot, CancellationToken.None);
        else if (!await new RevisionSwitchoverService(new OracleRevisionStore(() => new OracleConnection(options.ConnectionString)))
            .ActivateLexicalAsync(snapshot.Audit.RevisionId, snapshot.Audit))
            throw new InvalidDataException("Coverage audit failed; revision was not activated.");
    }
    return;
}

if (args.Length > 0 && args[0] == "batch-export")
{
    if (args.Length != 3 || args[1] != "--lang" || args[2] is not ("E" or "S")) throw new ArgumentException("Use batch-export --lang E|S.");
    var root = NwtEpubCorpusAdapter.FindRepositoryRoot();
    var snapshot = await CorpusSnapshotSerializer.ReadAsync(Path.Combine(SnapshotDirectory(root), $"nwt_{args[2]}.snapshot.json"));
    if (!snapshot.Audit.Passed) throw new InvalidDataException("Coverage audit failed.");
    var revision = snapshot.Audit.RevisionId;
    var shards = new BatchJsonlBuilder().Build(revision, snapshot.Chunks, Path.Combine(root, "data", "batch", args[2]));
    var manifestPath = Path.Combine(root, "data", "manifests", "nwt-audit-manifest.json");
    if (File.Exists(manifestPath))
    {
        using var manifestDoc = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(manifestDoc.RootElement.GetRawText())!.AsObject();
        manifest[args[2]]!["batch"] = JsonSerializer.SerializeToNode(new { shardCount = shards.Count, requestCount = shards.Sum(s => s.Requests),
            estimatedTokens = shards.Sum(s => s.EstimatedTokens), estimatedCostUsd = shards.Sum(s => s.EstimatedCostUsd),
            estimator = "ceil(UTF-8 input bytes / 4) per request; planning estimate, not tokenizer or billed usage",
            shards = shards.Select(s => new { requests = s.Requests, bytes = s.Bytes, estimatedTokens = s.EstimatedTokens, estimatedCostUsd = s.EstimatedCostUsd }).ToArray() });
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
    Console.WriteLine($"{args[2]}: {shards.Count} shards, {shards.Sum(s => s.Requests)} requests, {shards.Sum(s => s.EstimatedTokens)} estimated tokens, ${shards.Sum(s => s.EstimatedCostUsd):F4}");
    return;
}
if (args.Length > 0 && args[0] is "batch-submit" or "batch-status" or "batch-cancel" or "batch-import")
{
    if (args.Length < 3 || args[1] != "--lang" || args[2] is not ("E" or "S")) throw new ArgumentException("Use batch-submit|batch-status|batch-import --lang E|S [--output result.jsonl].");
    var root = NwtEpubCorpusAdapter.FindRepositoryRoot();
    var language = args[2];
    var directory = Path.Combine(root, "data", "batch", language);
    Directory.CreateDirectory(directory);
    var jobPath = Path.Combine(directory, "ingest-jobs.json");
    var jobs = File.Exists(jobPath) ? JsonSerializer.Deserialize<List<BatchCliJob>>(await File.ReadAllTextAsync(jobPath))! : new List<BatchCliJob>();
    var snapshot = await CorpusSnapshotSerializer.ReadAsync(Path.Combine(SnapshotDirectory(root), $"nwt_{language}.snapshot.json"));
    var revision = snapshot.Audit.RevisionId;
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    var service = new OpenAiBatchEmbeddingService(http);
    var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    if (args[0] == "batch-submit")
    {
        if (Environment.GetEnvironmentVariable("CORPUSLENS_ALLOW_BATCH_SUBMIT") != "yes") throw new InvalidOperationException("Set CORPUSLENS_ALLOW_BATCH_SUBMIT=yes for an authorized submission.");
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("OPENAI_API_KEY is required in the environment.");
        var shards = new BatchJsonlBuilder().Build(revision, snapshot.Chunks, directory);
        var pending = shards.Where(s => jobs.All(j => j.ShardPath != s.Path)).ToArray();
        var committed = Directory.GetFiles(Path.Combine(root, "data", "batch"), "ingest-jobs.json", SearchOption.AllDirectories)
            .SelectMany(file => JsonSerializer.Deserialize<List<BatchCliJob>>(File.ReadAllText(file)) ?? [])
            .Where(j => j.State is not "submission_failed")
            .Sum(j => j.EstimatedCostUsd);
        service.CheckBudget(pending, committed);
        foreach (var shard in pending)
        {
            var job = new BatchCliJob(revision, shard.Path, "submitting", null, null, shard.EstimatedTokens, shard.EstimatedCostUsd);
            jobs.Add(job); await SaveJobs();
            try
            {
                var batchId = await service.SubmitAsync(shard, key, CancellationToken.None);
                jobs[^1] = job with { State = "submitted", BatchId = batchId };
            }
            catch { jobs[^1] = job with { State = "submission_failed" }; await SaveJobs(); throw; }
            await SaveJobs();
        }
    }
    else if (args[0] == "batch-status")
    {
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("OPENAI_API_KEY is required in the environment.");
        for (var i = 0; i < jobs.Count; i++)
        {
            if (jobs[i].BatchId is null) continue;
            using var status = await service.StatusAsync(jobs[i].BatchId!, key, CancellationToken.None);
            var state = status.RootElement.GetProperty("status").GetString()!;
            var outputId = status.RootElement.TryGetProperty("output_file_id", out var output) && output.ValueKind == JsonValueKind.String ? output.GetString() : null;
            jobs[i] = jobs[i] with { State = state, OutputFileId = outputId };
            Console.WriteLine($"{jobs[i].BatchId}: {state}");
        }
        await SaveJobs();
    }
    else if (args[0] == "batch-cancel")
    {
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("OPENAI_API_KEY is required in the environment.");
        for (var i = 0; i < jobs.Count; i++)
        {
            if (jobs[i].BatchId is null || jobs[i].State is "completed" or "cancelled" or "failed" or "expired") continue;
            await service.CancelAsync(jobs[i].BatchId!, key, CancellationToken.None);
            jobs[i] = jobs[i] with { State = "cancelling" };
            Console.WriteLine($"{jobs[i].BatchId}: cancelling");
        }
        await SaveJobs();
    }
    else
    {
        if (args.Length != 4 || args[3] != "--download") throw new ArgumentException("Use batch-import --lang E|S --download.");
        if (jobs.Count == 0 || jobs.Any(j => j.State != "completed")) throw new InvalidOperationException("Every shard must have a completed ingest job.");
        if (string.IsNullOrWhiteSpace(key) || jobs.Any(j => j.OutputFileId is null)) throw new InvalidOperationException("Completed Batch outputs are required.");
        var lines = new List<string>();
        foreach (var job in jobs) lines.AddRange((await service.DownloadAsync(job.OutputFileId!, key, CancellationToken.None)).Split('\n'));
        var imported = new BatchOutputImporter().Parse(revision, "text-embedding-3-large-1024", snapshot.Chunks, lines);
        var options = OracleDatabaseOptions.FromEnvironment();
        var repository = new OracleCorpusRepository(() => new OracleConnection(options.ConnectionString));
        await repository.BulkInsertEmbeddingsAsync("text-embedding-3-large-1024", imported, CancellationToken.None);
        for (var i = 0; i < jobs.Count; i++) jobs[i] = jobs[i] with { State = "imported" };
        await SaveJobs();
        Console.WriteLine($"Imported {imported.Count} complete vectors for {language}; provider reported {imported.Sum(e => (long)e.TokenCount)} input tokens.");
    }
    return;
    async Task SaveJobs()
    {
        await File.WriteAllTextAsync(jobPath, JsonSerializer.Serialize(jobs, new JsonSerializerOptions { WriteIndented = true }));
        if (Environment.GetEnvironmentVariable("CORPUSLENS_ALLOW_ORACLE_CONNECT") == "yes")
        {
            var options = OracleDatabaseOptions.FromEnvironment();
            var repository = new OracleCorpusRepository(() => new OracleConnection(options.ConnectionString));
            foreach (var job in jobs)
                await repository.RecordIngestJobAsync(job.RevisionId, Path.GetFileName(job.ShardPath), job.State, job.BatchId, job.EstimatedTokens, job.EstimatedCostUsd, CancellationToken.None);
        }
    }
}

if (args.Length > 0 && args[0] == "direct-backfill")
{
    if (args.Length != 3 || args[1] != "--lang" || args[2] is not ("E" or "S")) throw new ArgumentException("Use direct-backfill --lang E|S.");
    var root = NwtEpubCorpusAdapter.FindRepositoryRoot();
    await DirectBackfill.RunAsync(args[2], root, SnapshotDirectory(root));
    return;
}

if (args.Length > 0 && args[0] == "phase6-verify")
{
    if (args.Length != 1 || Environment.GetEnvironmentVariable("CORPUSLENS_ALLOW_ORACLE_CONNECT") != "yes") throw new ArgumentException("Use phase6-verify with authorized Oracle access.");
    var options = OracleDatabaseOptions.FromEnvironment();
    await using var connection = new OracleConnection(options.ConnectionString);
    await connection.OpenAsync();
    await using (var counts = connection.CreateCommand())
    {
        counts.CommandText = "SELECT s.language,COUNT(c.id),COUNT(e.chunk_id) FROM search_chunk c JOIN source_revision r ON r.id=c.revision_id JOIN corpus_source s ON s.id=r.source_id LEFT JOIN chunk_embedding e ON e.chunk_id=c.id WHERE r.status='Active' GROUP BY s.language ORDER BY s.language";
        await using var reader = await counts.ExecuteReaderAsync();
        while (await reader.ReadAsync()) Console.WriteLine($"{reader.GetString(0)}: {Convert.ToInt64(reader.GetValue(2))} embeddings / {Convert.ToInt64(reader.GetValue(1))} chunks");
    }
    await using (var indexes = connection.CreateCommand())
    {
        indexes.CommandText = "SELECT index_name,index_type,status FROM user_indexes WHERE table_name='CHUNK_EMBEDDING' ORDER BY index_name";
        await using var reader = await indexes.ExecuteReaderAsync();
        while (await reader.ReadAsync()) Console.WriteLine($"{reader.GetString(0)}: {reader.GetString(1)} {reader.GetString(2)}");
    }
    return;
}

if (args.Length > 0 && args[0] == "snapshot")
{
    var languages = new List<string>();
    string? localPath = null;
    for (var i = 1; i < args.Length; i++)
    {
        if (args[i] == "--lang" && i + 1 < args.Length) languages.Add(args[++i].ToUpperInvariant());
        else if (args[i] == "--epub" && i + 1 < args.Length) localPath = args[++i];
        else throw new ArgumentException($"Unknown snapshot argument: {args[i]}");
    }
    if (languages.Count == 0 || languages.Any(l => l is not ("E" or "S")) || (localPath is not null && languages.Count != 1))
        throw new ArgumentException("Use snapshot --lang E [--lang S] [--epub local.epub].");
    var adapter = new NwtEpubCorpusAdapter();
    var repositoryRoot = NwtEpubCorpusAdapter.FindRepositoryRoot();
    IReadOnlyList<SourceArtifact>? discovered = null;
    var manifestPath = Path.Combine(repositoryRoot, "data", "manifests", "nwt-audit-manifest.json");
    var manifest = File.Exists(manifestPath)
        ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await File.ReadAllTextAsync(manifestPath))!.ToDictionary(k => k.Key, v => (object)v.Value)
        : new Dictionary<string, object>();
    foreach (var language in languages.Distinct())
    {
        SourceArtifact artifact;
        if (localPath is not null) artifact = new SourceArtifact(localPath, "", language, "New World Translation");
        else
        {
            discovered ??= await adapter.DiscoverAsync(CancellationToken.None);
            artifact = discovered.Single(a => a.Language == language);
        }
        var fetched = await adapter.FetchAsync(artifact, 0, CancellationToken.None);
        await using var sourceStream = fetched.Content;
        var parsed = await adapter.ParseAsync(fetched, CancellationToken.None);
        if (parsed.Errors.Count > 0) throw new InvalidDataException(string.Join("; ", parsed.Errors));
        var spans = parsed.Verses.Select(v => new TextSpan(v.SpanId, v.SpanId, v.Text, Hashes.Sha256(v.Text), null)).ToArray();
        var spanHash = Hashes.Sha256(string.Join("\n", spans.Select(s => s.Id + ":" + s.Sha256)));
        var revision = Hashes.Sha256($"{fetched.Artifact.Sha256}|{spanHash}|{CorpusVersions.Chunking}");
        var chunks = new InMemoryChunkingPipeline().Build(revision, parsed.Verses, parsed.Pericopes);
        var audit = new CoverageAuditor().Audit(revision, spans, chunks);
        var expected = parsed.ExpectedVerseIds ?? throw new InvalidDataException("Missing source verse inventory.");
        if (parsed.Verses.Count != expected.Count || parsed.Verses.Select(v => v.SpanId).Except(expected).Any()) throw new InvalidDataException("Source verse inventory mismatch.");
        // These conventional terminal verses are absent from the source navigation and
        // cannot be inferred by looking only between its first and last verse markers.
        var terminalOmissions = Enumerable.Range(9, 12).Select(n => BibleVerseId.Format(41, 16, n))
            .Append(BibleVerseId.Format(43, 7, 53));
        var omitted = parsed.Verses.GroupBy(v => (v.Book, v.Chapter)).SelectMany(g => Enumerable.Range(1, g.Max(v => v.Number)).Except(g.Select(v => v.Number))
            .Select(n => BibleVerseId.Format(g.Key.Book, g.Key.Chapter, n))).Concat(terminalOmissions).Distinct().Order().ToArray();
        if (parsed.Verses.Count + omitted.Length != 31102)
            throw new InvalidDataException($"Conventional verse reconciliation failed for {language}: {parsed.Verses.Count} + {omitted.Length} != 31102.");
        var books = parsed.Verses.Select(v => v.Book).Distinct().Count();
        var chapters = parsed.Verses.Select(v => (v.Book, v.Chapter)).Distinct().Count();
        if (books != 66 || chapters != 1189 || !audit.Passed) throw new InvalidDataException($"Audit failed: books={books}, chapters={chapters}, errors={string.Join(';', audit.Errors)} gaps={audit.Gaps.Count}");
        var snapshot = new CorpusSnapshot("1", language, fetched.Artifact.Edition, fetched.Artifact.Uri, fetched.Artifact.Sha256,
            "nwt-epub-v1", CorpusVersions.Normalization, "Watch Tower Bible and Tract Society of Pennsylvania", "Owner display/excerpt rights review pending",
            spanHash, parsed.Verses, parsed.Pericopes, spans, chunks, audit, omitted);
        var path = Path.Combine(SnapshotDirectory(repositoryRoot), $"nwt_{language}.snapshot.json");
        await CorpusSnapshotSerializer.WriteAsync(snapshot, path);
        var restored = await CorpusSnapshotSerializer.ReadAsync(path);
        if (restored.SourceSha256 != fetched.Artifact.Sha256 || restored.SpansSha256 != spanHash || restored.Verses.Count != parsed.Verses.Count ||
            !new CoverageAuditor().Audit(revision, restored.Spans, restored.Chunks).Passed)
            throw new InvalidDataException($"Snapshot round-trip audit failed for {language}.");
        await using var snapshotStream = File.OpenRead(path);
        var snapshotHash = Convert.ToHexString(await SHA256.HashDataAsync(snapshotStream)).ToLowerInvariant();
        var layerCounts = chunks.GroupBy(c => c.Layer).ToDictionary(g => g.Key, g => g.Count());
        manifest[language] = new { source = fetched.Artifact.Uri, edition = fetched.Artifact.Edition, epubSha256 = fetched.Artifact.Sha256,
            spansSha256 = spanHash, snapshotSha256 = snapshotHash, books, chapters, expectedNonemptyVerses = expected.Count,
            verses = parsed.Verses.Count, chunkCounts = layerCounts, characterCoveragePercent = 100,
            auditPassed = audit.Passed, auditErrors = audit.Errors, omittedVerseIds = omitted,
            omittedVerseExplanation = "The 31,102 conventional verse slots comprise all extracted nonempty verses plus the listed omissions. Mark 16:9-20 and John 7:53 are absent terminal slots in this EPUB's navigation; John 8:1-11 are note-only markers. Other listed IDs are absent verse slots in this edition. No missing ID is silently counted as indexed text." };
        Console.WriteLine($"{language}: {books} books, {chapters} chapters, {parsed.Verses.Count} verses, {chunks.Count} chunks, audit PASS");
    }
    Directory.CreateDirectory(Path.Combine(repositoryRoot, "data", "manifests"));
    await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddCorpusInfrastructure(builder.Configuration);
builder.Services.AddHostedService<IngestionWorker>();
builder.Services.AddHostedService<BatchPollingWorker>();
builder.Services.AddHostedService<CoverageGateWorker>();
builder.Build().Run();

static string SnapshotDirectory(string root) => Environment.GetEnvironmentVariable("Corpus__SnapshotPath") ?? Path.Combine(root, "data", "snapshots");

internal sealed record BatchCliJob(string RevisionId, string ShardPath, string State, string? BatchId, string? OutputFileId, long EstimatedTokens, decimal EstimatedCostUsd);

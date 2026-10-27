using CorpusLens.Infrastructure;
using CorpusLens.Worker;
using CorpusLens.Core;
using System.Security.Cryptography;
using System.Text.Json;

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
    var manifest = new Dictionary<string, object>();
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
        var omitted = parsed.Verses.GroupBy(v => (v.Book, v.Chapter)).SelectMany(g => Enumerable.Range(1, g.Max(v => v.Number)).Except(g.Select(v => v.Number))
            .Select(n => BibleVerseId.Format(g.Key.Book, g.Key.Chapter, n))).ToArray();
        var books = parsed.Verses.Select(v => v.Book).Distinct().Count();
        var chapters = parsed.Verses.Select(v => (v.Book, v.Chapter)).Distinct().Count();
        if (books != 66 || chapters != 1189 || !audit.Passed) throw new InvalidDataException($"Audit failed: books={books}, chapters={chapters}, errors={string.Join(';', audit.Errors)} gaps={audit.Gaps.Count}");
        var snapshot = new CorpusSnapshot("1", language, fetched.Artifact.Edition, fetched.Artifact.Uri, fetched.Artifact.Sha256,
            "nwt-epub-v1", CorpusVersions.Normalization, "Watch Tower Bible and Tract Society of Pennsylvania", "Owner display/excerpt rights review pending",
            spanHash, parsed.Verses, parsed.Pericopes, spans, chunks, audit, omitted);
        var path = Path.Combine(repositoryRoot, "data", "snapshots", $"nwt_{language}.snapshot.json");
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
            auditPassed = audit.Passed, auditErrors = audit.Errors, omittedVerseIds = omitted };
        Console.WriteLine($"{language}: {books} books, {chapters} chapters, {parsed.Verses.Count} verses, {chunks.Count} chunks, audit PASS");
    }
    Directory.CreateDirectory(Path.Combine(repositoryRoot, "data", "manifests"));
    await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "data", "manifests", "nwt-audit-manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddCorpusInfrastructure(builder.Configuration);
builder.Services.AddHostedService<IngestionWorker>();
builder.Services.AddHostedService<BatchPollingWorker>();
builder.Services.AddHostedService<CoverageGateWorker>();
builder.Services.AddHostedService<AdbKeepAliveWorker>();
builder.Build().Run();

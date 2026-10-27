using System.Security.Cryptography;
using System.Text;

namespace CorpusLens.Core;

public static class CorpusVersions
{
    public const string Identity = "bible-bbcccvvv-v1";
    public const string Normalization = "unicode-whitespace-case-v1";
    public const string Chunking = "four-layers-v1";
}

public record CorpusSource(string Id, string Name, string Language, string Edition, string? RightsAssessment);
public record SourceRevision(string Id, string SourceId, string ArtifactSha256, string ParserVersion, string NormalizationVersion, bool IsActive = false);
public record Document(string Id, string RevisionId, string NativeId, string Title, string Language);
public record DocumentNode(string Id, string DocumentId, string? ParentId, string Kind, string NativeId, string? AttributesJson);
public record TextSpan(string Id, string NodeId, string Text, string Sha256, string? SourceLocator);
public record SearchChunk(string Id, string RevisionId, string Layer, string EmbeddingInput, string InputSha256, IReadOnlyList<ChunkSegment> Segments);
public record ChunkSegment(string ChunkId, string TextSpanId, int Start, int End);
public record EmbeddingModel(string Id, string Provider, string Name, int Dimensions, string NormalizationVersion);
public record ChunkEmbedding(string ChunkId, string ModelId, float[] Vector, int TokenCount);
public record IngestJob(string Id, string RevisionId, string State, DateTimeOffset CreatedAt, string? ExternalBatchId);
public record CoverageGap(string TextSpanId, int Start, int End);
public record CoverageAuditReport(string RevisionId, bool Passed, IReadOnlyList<CoverageGap> Gaps, IReadOnlyList<string> Errors);

public static class Hashes
{
    public static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public record Verse(int Book, int Chapter, int Number, string Text, string BookName, string SpanId);
public record Pericope(int Book, int Chapter, int StartVerse, int EndVerse, string Heading);

public static class BibleVerseId
{
    public static string Format(int book, int chapter, int verse)
    {
        if (book is < 1 or > 66 || chapter is < 1 or > 999 || verse is < 1 or > 999)
            throw new ArgumentOutOfRangeException(nameof(verse), "Book, chapter, or verse is outside BBCCCVVV bounds.");
        return $"{book:00}{chapter:000}{verse:000}";
    }
}

public record BibleLinkConfig(string JwLocale, string WolLanguage, string WolLibrary, string Publication);

public static class BibleLinks
{
    public static (string Jw, string Wol)? ForVerse(int book, int chapter, int verse, BibleLinkConfig? config)
    {
        if (config is null) return null;
        var id = BibleVerseId.Format(book, chapter, verse);
        return ($"https://www.jw.org/finder?wtlocale={Uri.EscapeDataString(config.JwLocale)}&bible={id}&pub={Uri.EscapeDataString(config.Publication)}",
            $"https://wol.jw.org/{Uri.EscapeDataString(config.WolLanguage)}/wol/b/{config.WolLibrary}/{Uri.EscapeDataString(config.Publication)}/{book}/{chapter}#v={book}:{chapter}:{verse}");
    }
}

public record SourceArtifact(string Uri, string Sha256, string Language, string Edition);
public record SourceFetchResult(Stream Content, SourceArtifact Artifact);
public record ParseResult(IReadOnlyList<Verse> Verses, IReadOnlyList<Pericope> Pericopes, IReadOnlyList<string> Errors);
public interface ICorpusSourceAdapter
{
    Task<IReadOnlyList<SourceArtifact>> DiscoverAsync(CancellationToken cancellationToken);
    Task<SourceFetchResult> FetchAsync(SourceArtifact artifact, long offset, CancellationToken cancellationToken);
    Task<ParseResult> ParseAsync(SourceFetchResult source, CancellationToken cancellationToken);
}
public interface IChunkingPipeline { IReadOnlyList<SearchChunk> Build(string revisionId, IReadOnlyList<Verse> verses, IReadOnlyList<Pericope> pericopes); }
public interface ICoverageAuditor { CoverageAuditReport Audit(string revisionId, IReadOnlyList<TextSpan> spans, IReadOnlyList<SearchChunk> chunks); }
public interface IEmbeddingBatchClient { Task<string> SubmitAsync(IReadOnlyList<SearchChunk> chunks, CancellationToken cancellationToken); Task<string> PollAsync(string batchId, CancellationToken cancellationToken); }
public record SearchRequest(string Query, string Language, string? Edition, int Limit = 10);
public record SearchHit(string ChunkId, string Text, double Score, IReadOnlyList<string> VerseIds);
public interface ISearchRepository { Task<IReadOnlyList<SearchHit>> SearchAsync(SearchRequest request, float[]? queryVector, CancellationToken cancellationToken); }
public interface IVectorStore { Task UpsertAsync(IReadOnlyList<ChunkEmbedding> embeddings, CancellationToken cancellationToken); Task<float[]?> GetQueryVectorAsync(string key, CancellationToken cancellationToken); }

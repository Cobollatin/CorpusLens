using System.Text.Json;

namespace CorpusLens.Core;

public record CorpusSnapshot(
    string SchemaVersion, string Language, string Edition, string SourceUri, string SourceSha256,
    string ParserVersion, string NormalizationVersion, string Attribution, string? RightsAssessment,
    string SpansSha256, IReadOnlyList<Verse> Verses, IReadOnlyList<Pericope> Pericopes,
    IReadOnlyList<TextSpan> Spans, IReadOnlyList<SearchChunk> Chunks, CoverageAuditReport Audit,
    IReadOnlyList<string> OmittedVerseIds);

public static class CorpusSnapshotSerializer
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static async Task WriteAsync(CorpusSnapshot snapshot, string path, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, snapshot, Options, cancellationToken);
    }
    public static async Task<CorpusSnapshot> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<CorpusSnapshot>(stream, Options, cancellationToken)
            ?? throw new InvalidDataException("Empty corpus snapshot.");
    }
}

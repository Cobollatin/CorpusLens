namespace CorpusLens.Core;

public sealed class InMemoryChunkingPipeline : IChunkingPipeline
{
    public IReadOnlyList<SearchChunk> Build(string revisionId, IReadOnlyList<Verse> verses, IReadOnlyList<Pericope> pericopes)
    {
        var result = new List<SearchChunk>();
        var groups = verses.OrderBy(v => v.Book).ThenBy(v => v.Chapter).ThenBy(v => v.Number)
            .GroupBy(v => (v.Book, v.Chapter));
        foreach (var group in groups)
        {
            var chapter = group.ToArray();
            foreach (var verse in chapter) Add("verse", [verse], "");
            Windows("window3", 3, 1);
            Windows("window5", 5, 2);
            foreach (var section in pericopes.Where(p => p.Book == group.Key.Book && p.Chapter == group.Key.Chapter))
            {
                var included = chapter.Where(v => v.Number >= section.StartVerse && v.Number <= section.EndVerse).ToArray();
                if (included.Length > 0) Add("pericope", included, $"{included[0].BookName} {group.Key.Chapter} | {section.Heading} | {included[0].Number}-{included[^1].Number}\n");
            }
            void Windows(string layer, int width, int step)
            {
                for (var i = 0; i < chapter.Length; i += step)
                    Add(layer, chapter.Skip(i).Take(width).ToArray(), "");
            }
        }
        return result;

        void Add(string layer, Verse[] selection, string header)
        {
            if (selection.Length == 0) return;
            var body = string.Join(" ", selection.Select(v => v.Text));
            var input = header + body;
            var id = Hashes.Sha256($"{CorpusVersions.Chunking}|{revisionId}|{layer}|{selection[0].SpanId}|{selection[^1].SpanId}|{header}");
            result.Add(new SearchChunk(id, revisionId, layer, input, Hashes.Sha256(input),
                selection.Select(v => new ChunkSegment(id, v.SpanId, 0, v.Text.Length)).ToArray()));
        }
    }
}

public sealed class CoverageAuditor : ICoverageAuditor
{
    public CoverageAuditReport Audit(string revisionId, IReadOnlyList<TextSpan> spans, IReadOnlyList<SearchChunk> chunks)
    {
        var gaps = new List<CoverageGap>();
        var errors = new List<string>();
        foreach (var span in spans)
        {
            if (Hashes.Sha256(span.Text) != span.Sha256) errors.Add($"Hash mismatch: {span.Id}");
            var intervals = chunks.SelectMany(c => c.Segments).Where(s => s.TextSpanId == span.Id).OrderBy(s => s.Start).ToArray();
            var covered = 0;
            foreach (var segment in intervals)
            {
                if (segment.Start < 0 || segment.End > span.Text.Length || segment.Start >= segment.End)
                { errors.Add($"Invalid interval: {span.Id}"); continue; }
                if (segment.Start > covered) gaps.Add(new CoverageGap(span.Id, covered, segment.Start));
                covered = Math.Max(covered, segment.End);
            }
            if (covered < span.Text.Length) gaps.Add(new CoverageGap(span.Id, covered, span.Text.Length));
        }
        var known = spans.Select(s => s.Id).ToHashSet();
        foreach (var segment in chunks.SelectMany(c => c.Segments).Where(s => !known.Contains(s.TextSpanId)))
            errors.Add($"Unknown span: {segment.TextSpanId}");
        return new CoverageAuditReport(revisionId, gaps.Count == 0 && errors.Count == 0, gaps, errors);
    }
}

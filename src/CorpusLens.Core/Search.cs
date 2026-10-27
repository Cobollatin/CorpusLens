using System.Globalization;
using System.Text.RegularExpressions;

namespace CorpusLens.Core;

public sealed record Citation(int Book, int Chapter, int StartVerse, int EndVerse, string BookName)
{
    public IReadOnlyList<string> VerseIds => Enumerable.Range(StartVerse, EndVerse - StartVerse + 1).Select(v => BibleVerseId.Format(Book, Chapter, v)).ToArray();
}

public sealed class CitationQueryParser
{
    private static readonly string[][] Names = [
        ["genesis", "gen", "génesis"], ["exodus", "ex", "éxodo", "exodo"], ["leviticus", "lev", "levítico"], ["numbers", "num", "números"], ["deuteronomy", "deut", "deuteronomio"],
        ["joshua", "jos", "josué"], ["judges", "judg", "jueces"], ["ruth", "rut"], ["1 samuel"], ["2 samuel"], ["1 kings", "1 reyes"], ["2 kings", "2 reyes"], ["1 chronicles"], ["2 chronicles"], ["ezra"], ["nehemiah"], ["esther"], ["job"], ["psalms", "psalm", "ps", "salmos", "salmo", "sal"], ["proverbs", "proverbios", "prov"], ["ecclesiastes", "eclesiastés"], ["song of solomon", "cantar de los cantares"], ["isaiah", "isaías", "isa"], ["jeremiah", "jeremías"], ["lamentations"], ["ezekiel", "ezequiel"], ["daniel"], ["hosea", "oseas"], ["joel"], ["amos", "amós"], ["obadiah", "abdías"], ["jonah", "jonás"], ["micah", "miqueas"], ["nahum", "nahúm"], ["habakkuk", "habacuc"], ["zephaniah", "sofonías"], ["haggai", "ageo"], ["zechariah", "zacarías"], ["malachi", "malaquías"],
        ["matthew", "matt", "mt", "mateo"], ["mark", "marcos"], ["luke", "lucas"], ["john", "jn", "juan"], ["acts", "hechos"], ["romans", "rom", "romanos"], ["1 corinthians", "1 corintios"], ["2 corinthians", "2 corintios"], ["galatians", "gálatas"], ["ephesians", "efesios"], ["philippians", "filipenses"], ["colossians", "colosenses"], ["1 thessalonians"], ["2 thessalonians"], ["1 timothy"], ["2 timothy"], ["titus", "tito"], ["philemon", "filemón"], ["hebrews", "hebreos"], ["james", "santiago"], ["1 peter", "1 pedro"], ["2 peter", "2 pedro"], ["1 john", "1 juan"], ["2 john", "2 juan"], ["3 john", "3 juan"], ["jude", "judas"], ["revelation", "apocalipsis"] ];
    private static readonly Dictionary<string, (int Book, string Name)> Books = Names.SelectMany((names, index) => names.Select(name => new KeyValuePair<string, (int, string)>(Fold(name), (index + 1, names[0])))).GroupBy(x => x.Key).ToDictionary(x => x.Key, x => x.First().Value);
    private static string Fold(string value) => new string(value.Normalize(System.Text.NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant().Trim().TrimEnd('.');
    public Citation? Parse(string query)
    {
        var match = Regex.Match(query.Trim(), @"^(?<book>(?:[1-3]\s*)?[\p{L}. ]+?)\s+(?<chapter>\d{1,3})\s*:\s*(?<start>\d{1,3})(?:\s*[-–]\s*(?<end>\d{1,3}))?$", RegexOptions.CultureInvariant);
        if (!match.Success || !Books.TryGetValue(Fold(match.Groups["book"].Value), out var book)) return null;
        var chapter = int.Parse(match.Groups["chapter"].Value); var start = int.Parse(match.Groups["start"].Value);
        var end = match.Groups["end"].Success ? int.Parse(match.Groups["end"].Value) : start;
        if (chapter < 1 || start < 1 || end < start || end - start > 100) return null;
        return new Citation(book.Book, chapter, start, end, book.Name);
    }
}

public sealed record SearchCandidate(string ChunkId, string Layer, IReadOnlyList<string> VerseIds, double? DenseScore, double? LexicalScore, int? DenseRank, int? LexicalRank, string Language = "E", double? PrecomputedTotal = null, double? PrecomputedDense = null, double? PrecomputedLexical = null);
public sealed record PassageVerse(string Id, int Number, string Text, bool IsPrimaryHighlight, string? JwLink, string? WolLink);
public sealed record SearchResult(string Reference, string BookName, int Chapter, string? Section, double Score, double DenseRrf, double LexicalRrf, IReadOnlyList<string> Layers, IReadOnlyList<PassageVerse> Verses);
public sealed record SearchResponse(IReadOnlyList<SearchResult> Results, Citation? Citation, bool DegradedToLexicalOnly, string CacheState, string CoverageStatus, string Mode);
public sealed record HybridSearchOptions(double VectorWeight = 0.8, double LexicalWeight = 0.2, double LayerBoost = 0.05, int RrfK = 60, double CorroborationBoost = 0.04);

public static class ReciprocalRankFusion
{
    public static (double Total, double Dense, double Lexical) Score(SearchCandidate candidate, HybridSearchOptions options)
    {
        if (candidate.PrecomputedTotal is double total) return (total, candidate.PrecomputedDense ?? 0, candidate.PrecomputedLexical ?? 0);
        var dense = candidate.DenseRank is > 0 ? options.VectorWeight / (options.RrfK + candidate.DenseRank.Value) : 0;
        var lexical = candidate.LexicalRank is > 0 ? options.LexicalWeight / (options.RrfK + candidate.LexicalRank.Value) : 0;
        return ((dense + lexical) * (candidate.Layer == "SingleVerse" ? 1 + options.LayerBoost : 1), dense, lexical);
    }
}

public interface IReranker { Task<IReadOnlyList<SearchCandidate>> RerankAsync(string query, IReadOnlyList<SearchCandidate> candidates, CancellationToken token); }
public sealed class NoOpReranker : IReranker
{
    public Task<IReadOnlyList<SearchCandidate>> RerankAsync(string query, IReadOnlyList<SearchCandidate> candidates, CancellationToken token) => Task.FromResult(candidates);
}
// A bounded ONNX cross-encoder can implement this interface without adding a network provider to the request path.
public interface IOnnxCrossEncoder : IReranker { }

public sealed class PassageDeduplicator
{
    public IReadOnlyList<SearchResult> Build(IReadOnlyList<SearchCandidate> candidates, IReadOnlyDictionary<string, Verse> verses, IReadOnlyList<Pericope> pericopes, string language, HybridSearchOptions options, Citation? citation, int limit)
    {
        var groups = new List<List<SearchCandidate>>();
        foreach (var hit in candidates.OrderByDescending(c => ReciprocalRankFusion.Score(c, options).Total))
        {
            var ids = hit.VerseIds.Where(verses.ContainsKey).ToArray();
            if (ids.Length == 0) continue;
            var matching = groups.Where(g => g.Any(other => other.VerseIds.Intersect(ids).Any()) && verses[g[0].VerseIds.First()].Book == verses[ids[0]].Book && verses[g[0].VerseIds.First()].Chapter == verses[ids[0]].Chapter).ToList();
            var target = matching.FirstOrDefault() ?? new List<SearchCandidate>();
            if (matching.Count == 0) groups.Add(target);
            foreach (var extra in matching.Skip(1)) { target.AddRange(extra); groups.Remove(extra); }
            target.Add(hit);
        }
        return groups.Select(group =>
        {
            var all = group.SelectMany(g => g.VerseIds).Where(verses.ContainsKey).Distinct().Order().ToArray();
            var first = verses[all[0]]; var last = verses[all[^1]];
            var scores = group.Select(c => ReciprocalRankFusion.Score(c, options)).ToArray();
            var anchors = citation is not null && citation.Book == first.Book && citation.Chapter == first.Chapter
                ? citation.VerseIds.Intersect(all).ToHashSet() : group.Where(g => g.Layer == "SingleVerse").OrderByDescending(g => ReciprocalRankFusion.Score(g, options).Total).Take(1).SelectMany(g => g.VerseIds).ToHashSet();
            if (anchors.Count == 0) anchors.Add(all[0]);
            var passage = Enumerable.Range(first.Number, last.Number - first.Number + 1).Select(n => BibleVerseId.Format(first.Book, first.Chapter, n)).Where(verses.ContainsKey).Select(id =>
            {
                var v = verses[id]; var links = BibleLinks.ForVerse(v.Book, v.Chapter, v.Number, language);
                return new PassageVerse(id, v.Number, v.Text, anchors.Contains(id), links.Jw, links.Wol);
            }).ToArray();
            var section = pericopes.LastOrDefault(p => p.Book == first.Book && p.Chapter == first.Chapter && p.StartVerse <= first.Number && p.EndVerse >= first.Number)?.Heading;
            var reference = $"{first.BookName} {first.Chapter}:{first.Number}" + (last.Number != first.Number ? $"-{last.Number}" : "");
            var boost = citation is not null && citation.VerseIds.Intersect(all).Any() ? 1.0 : 0;
            // Overlapping chunks describe the same passage; summing them rewards chunk count over relevance.
            var layers = group.Select(g => g.Layer).Distinct().ToArray();
            var corroboration = layers.Count(l => l != "PericopeSection") >= 2 ? options.CorroborationBoost : 0;
            return new SearchResult(reference, first.BookName, first.Chapter, section, scores.Max(s => s.Total) * (1 + corroboration) + boost, scores.Max(s => s.Dense), scores.Max(s => s.Lexical), layers, passage);
        }).OrderByDescending(r => r.Score).Take(limit).ToArray();
    }
}

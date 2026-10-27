using System.Text.Json;

namespace CorpusLens.Core;

public sealed record EvalCase(string Id, string Language, string Category, string Query, IReadOnlyList<string> ExpectedVerseIds);
public sealed record EvalMetric(int Count, double RecallAt5, double RecallAt10, double MrrAt10, double NdcgAt10);
public static class EvaluationRunner
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    public static async Task<IReadOnlyList<EvalCase>> LoadAsync(string path)
    {
        await using var file = File.OpenRead(path);
        var cases = await JsonSerializer.DeserializeAsync<EvalCase[]>(file, Json) ?? throw new InvalidDataException("Empty evaluation set.");
        if (cases.Length == 0 || cases.Any(c => c.ExpectedVerseIds.Count == 0 || c.ExpectedVerseIds.Any(id => id.Length != 8))) throw new InvalidDataException("Invalid evaluation set.");
        return cases;
    }
    public static EvalMetric Compute(IEnumerable<(EvalCase Case, IReadOnlyList<string> RankedIds)> rows)
    {
        var scores = rows.Select(row =>
        {
            var expected = row.Case.ExpectedVerseIds.ToHashSet();
            var ranked = row.RankedIds.Take(10).ToArray();
            double recall5 = ranked.Take(5).Count(expected.Contains) / (double)expected.Count;
            double recall10 = ranked.Count(expected.Contains) / (double)expected.Count;
            var first = Array.FindIndex(ranked, expected.Contains);
            var mrr = first < 0 ? 0 : 1.0 / (first + 1);
            var dcg = ranked.Select((id, i) => expected.Contains(id) ? 1.0 / Math.Log2(i + 2) : 0).Sum();
            var ideal = Enumerable.Range(0, Math.Min(10, expected.Count)).Sum(i => 1.0 / Math.Log2(i + 2));
            return (recall5, recall10, mrr, ndcg: ideal == 0 ? 0 : dcg / ideal);
        }).ToArray();
        return new(scores.Length, scores.Average(x => x.recall5), scores.Average(x => x.recall10), scores.Average(x => x.mrr), scores.Average(x => x.ndcg));
    }
}

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CorpusLens.Infrastructure;

public static class LexicalQuery
{
    private static readonly HashSet<string> English = new(StringComparer.Ordinal)
    {
        "a", "about", "an", "and", "are", "as", "at", "be", "by", "can", "do", "does", "for", "from", "how", "i", "in", "is", "it", "of", "on", "or", "the", "to", "was", "were", "what", "when", "where", "which", "who", "why", "with"
    };
    private static readonly HashSet<string> Spanish = new(StringComparer.Ordinal)
    {
        "a", "al", "ante", "como", "con", "de", "del", "el", "en", "es", "esta", "este", "la", "las", "le", "les", "lo", "los", "para", "por", "que", "se", "su", "sus", "un", "una", "y"
    };

    public static string[] Terms(string query, string language)
    {
        var folded = new string(query.Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray())
            .ToLowerInvariant();
        var all = Regex.Matches(folded, @"[\p{L}\p{N}]+")
            .Select(m => m.Value).Where(t => t.Length > 1 || char.IsDigit(t[0]))
            .Distinct(StringComparer.Ordinal).Take(32).ToArray();
        var stopwords = language == "S" ? Spanish : English;
        var content = all.Where(t => !stopwords.Contains(t)).ToArray();
        return content.Length > 0 ? content : all;
    }

    public static string OracleText(string query, string language)
    {
        var terms = Terms(query, language);
        return terms.Length == 0 ? "{zzzzzzzz}" : string.Join(" ACCUM ", terms.Select(t => "{" + t + "}"));
    }

    public static double CoverageWeight(IReadOnlyList<string> queryTerms, IReadOnlyDictionary<string, int> documentTerms) =>
        queryTerms.Count > 1 && queryTerms.Count(documentTerms.ContainsKey) == 1 ? .25 : 1;
}

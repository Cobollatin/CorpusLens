using CorpusLens.Core;

namespace CorpusLens.UnitTests;

public class CoreTests
{
    [Fact]
    public void John316LinksUseConfiguredLocaleAndStableId()
    {
        Assert.Equal("43003016", BibleVerseId.Format(43, 3, 16));
        var links = BibleLinks.ForVerse(43, 3, 16, new BibleLinkConfig("E", "en", "r1/lp-e", "nwtsty"));
        Assert.Equal("https://www.jw.org/finder?wtlocale=E&bible=43003016&pub=nwtsty", links!.Value.Jw);
        Assert.Equal("https://wol.jw.org/en/wol/b/r1/lp-e/nwtsty/43/3#v=43:3:16", links.Value.Wol);
        Assert.Null(BibleLinks.ForVerse(43, 3, 16, null));
    }

    [Fact]
    public void CoverageAuditFindsGapAndAcceptsOverlap()
    {
        var span = new TextSpan("s", "n", "abcdef", Hashes.Sha256("abcdef"), null);
        var gap = new SearchChunk("c", "r", "verse", "abc", Hashes.Sha256("abc"), [new ChunkSegment("c", "s", 0, 3), new ChunkSegment("c", "s", 4, 6)]);
        var audit = new CoverageAuditor();
        Assert.Equal(new CoverageGap("s", 3, 4), Assert.Single(audit.Audit("r", [span], [gap]).Gaps));
        var overlap = gap with { Segments = [new ChunkSegment("c", "s", 0, 4), new ChunkSegment("c", "s", 3, 6)] };
        Assert.True(audit.Audit("r", [span], [overlap]).Passed);
    }

    [Fact]
    public void FourLayersStayWithinChapterAndCoverEveryVerse()
    {
        var verses = Enumerable.Range(1, 6).Select(n => new Verse(43, 3, n, $"verse {n}", "John", $"s{n}"))
            .Concat([new Verse(43, 4, 1, "next chapter", "John", "s7")]).ToArray();
        var sections = new[] { new Pericope(43, 3, 1, 6, "Teaching"), new Pericope(43, 4, 1, 1, "Travel") };
        var chunks = new InMemoryChunkingPipeline().Build("r", verses, sections);
        Assert.Equal(7, chunks.Count(c => c.Layer == "verse"));
        Assert.Equal(7, chunks.Count(c => c.Layer == "window3"));
        Assert.Equal(4, chunks.Count(c => c.Layer == "window5"));
        Assert.Equal(2, chunks.Count(c => c.Layer == "pericope"));
        Assert.All(chunks, c => Assert.Single(c.Segments.Select(s => s.TextSpanId == "s7" ? 4 : 3).Distinct()));
        var spans = verses.Select(v => new TextSpan(v.SpanId, "n", v.Text, Hashes.Sha256(v.Text), null)).ToArray();
        Assert.True(new CoverageAuditor().Audit("r", spans, chunks).Passed);
    }
}

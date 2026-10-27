using CorpusLens.Core;
using CorpusLens.Infrastructure;

namespace CorpusLens.UnitTests;

public class Phase2Tests
{
    private const string Xhtml = """
        <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><body>
        <p class="w_navigation">Navigation</p><header><h1>Book</h1></header>
        <p><span id="chapter1_verse1"/><strong><sup>1</sup></strong>First <em>word</em><a epub:type="noteref" href="#n">*</a> remains.
        <span id="chapter1_verse2"/><strong><sup>2</sup></strong> Second text.</p>
        <p>Continued thought.</p><p><span id="chapter1_verse3"/><strong><sup>3</sup></strong>Third.</p>
        <aside epub:type="footnote"><p>Footnote body</p></aside></body></html>
        """;

    [Fact]
    public void XhtmlSplitsVersesAndKeepsContinuationWithoutNoteMarkers()
    {
        var (verses, omitted) = NwtEpubCorpusAdapter.ParseChapterXhtml(Xhtml, 1, 1, "Genesis");
        Assert.Empty(omitted);
        Assert.Equal(3, verses.Count);
        Assert.Equal("First word remains.", verses[0].Text);
        Assert.Equal("Second text. Continued thought.", verses[1].Text);
        Assert.Equal("Third.", verses[2].Text);
        Assert.DoesNotContain("Footnote", string.Join(" ", verses.Select(v => v.Text)));
    }

    [Fact]
    public void NoteOnlyVerseIsDocumentedButUnexpectedEmptyVerseFails()
    {
        var xhtml = """<html xmlns:epub="http://www.idpf.org/2007/ops"><body><p><span id="chapter1_verse1"/>Text.<span id="chapter1_verse2"/><sup>2</sup><a epub:type="noteref">*</a></p></body></html>""";
        var (verses, omitted) = NwtEpubCorpusAdapter.ParseChapterXhtml(xhtml, 1, 1, "Genesis");
        Assert.Single(verses);
        Assert.Equal("01001002", Assert.Single(omitted));
        Assert.Throws<InvalidDataException>(() => NwtEpubCorpusAdapter.ParseChapterXhtml(xhtml.Replace("<a epub:type=\"noteref\">*</a>", ""), 1, 1, "Genesis"));
    }

    [Fact]
    public void OutlineSectionsComeFromSourceRanges()
    {
        var outline = """<html><body><ul class="outline"><li class="L1 chapterNo"><p><a>1</a></p><ul><li class="L2"><p>Creation <span class="altsize">(</span><a><span>1-2</span></a></p></li><li class="L2"><p>Rest <span class="altsize">(</span><a><span>3</span></a></p></li></ul></li></ul></body></html>""";
        var verses = NwtEpubCorpusAdapter.ParseChapterXhtml(Xhtml, 1, 1, "Genesis").Verses;
        var sections = NwtEpubCorpusAdapter.ParseOutlineXhtml(outline, 1, verses);
        Assert.Equal([new Pericope(1, 1, 1, 2, "Creation"), new Pericope(1, 1, 3, 3, "Rest")], sections);
        var chunks = new InMemoryChunkingPipeline().Build("r", verses, sections);
        Assert.Equal(2, chunks.Count(c => c.Layer == "PericopeSection"));
        Assert.Equal("[Genesis | 1 | Creation | 1-2]", chunks.Single(c => c.Layer == "PericopeSection" && c.Segments.Count == 2).ContextHeader);
    }

    [Theory]
    [InlineData("E", "wtlocale=E", "/en/wol/b/r1/lp-e/nwtsty/")]
    [InlineData("S", "wtlocale=S", "/es/wol/b/r4/lp-s/nwtsty/")]
    public void SingleAndRangeLinksUseStableIds(string language, string locale, string wolPath)
    {
        var single = BibleLinks.ForVerse(43, 3, 16, language);
        Assert.Contains(locale, single.Jw);
        Assert.Contains("43003016", single.Jw);
        Assert.Contains(wolPath, single.Wol);
        var range = BibleLinks.ForRange(43, 3, 16, 43, 3, 18, language);
        Assert.Contains("43003016-43003018", range.Jw);
        Assert.Contains("#v=43:3:16-43:3:18", range.Wol);
    }

    [Fact]
    public void WindowSegmentsNeverCrossChapterAndAuditDetectsTampering()
    {
        var verses = Enumerable.Range(1, 6).Select(n => new Verse(1, 1, n, $"Verse {n}", "Genesis", BibleVerseId.Format(1, 1, n)))
            .Append(new Verse(1, 2, 1, "Next", "Genesis", BibleVerseId.Format(1, 2, 1))).ToArray();
        var chunks = new InMemoryChunkingPipeline().Build("r", verses, [new Pericope(1, 1, 1, 6, "First"), new Pericope(1, 2, 1, 1, "Second")]);
        Assert.Equal(4, chunks.Count(c => c.Layer == "SlidingWindow5Step2"));
        Assert.All(chunks, c => Assert.Single(c.Segments.Select(s => s.TextSpanId[..5]).Distinct()));
        var spans = verses.Select(v => new TextSpan(v.SpanId, v.SpanId, v.Text, Hashes.Sha256(v.Text), null)).ToArray();
        Assert.True(new CoverageAuditor().Audit("r", spans, chunks).Passed);
        Assert.False(new CoverageAuditor().Audit("r", spans, []).Passed);
    }

    [Fact]
    public async Task SnapshotRoundTripsWithCoverageAndHashChain()
    {
        var verse = new Verse(1, 1, 1, "Beginning", "Genesis", "01001001");
        var span = new TextSpan(verse.SpanId, verse.SpanId, verse.Text, Hashes.Sha256(verse.Text), null);
        var chunks = new InMemoryChunkingPipeline().Build("revision", [verse], [new Pericope(1, 1, 1, 1, "Creation")]);
        var audit = new CoverageAuditor().Audit("revision", [span], chunks);
        var snapshot = new CorpusSnapshot("1", "E", "NWT", "local", "epubhash", "parser", "normalizer", "attribution", null,
            Hashes.Sha256(span.Id + ":" + span.Sha256), [verse], [new Pericope(1, 1, 1, 1, "Creation")], [span], chunks, audit, []);
        var path = Path.Combine(Path.GetTempPath(), $"corpuslens-{Guid.NewGuid():N}.json");
        try
        {
            await CorpusSnapshotSerializer.WriteAsync(snapshot, path);
            var restored = await CorpusSnapshotSerializer.ReadAsync(path);
            Assert.Equal(snapshot.SpansSha256, restored.SpansSha256);
            Assert.Equal(snapshot.Verses, restored.Verses);
            Assert.True(new CoverageAuditor().Audit("revision", restored.Spans, restored.Chunks).Passed);
        }
        finally { File.Delete(path); }
    }
}

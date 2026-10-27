using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CorpusLens.Core;

namespace CorpusLens.Infrastructure;

public sealed class NwtEpubCorpusAdapter : ICorpusSourceAdapter
{
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    private static readonly Regex VerseMarker = new(@"^chapter(\d+)_verse(\d+)$", RegexOptions.Compiled);
    private readonly HttpClient _http;
    private readonly string _rawDirectory;

    public NwtEpubCorpusAdapter(HttpClient? http = null, string? rawDirectory = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _rawDirectory = rawDirectory ?? Path.Combine(FindRepositoryRoot(), "data", "raw");
    }

    public async Task<IReadOnlyList<SourceArtifact>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var artifacts = new List<SourceArtifact>();
        foreach (var lang in new[] { "E", "S" })
        {
            var url = $"https://b.jw-cdn.org/apis/pub-media/GETPUBMEDIALINKS?output=json&pub=nwt&fileformat=EPUB&alllangs=0&langwritten={lang}";
            using var response = await _http.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            var file = json.RootElement.GetProperty("files").GetProperty(lang).GetProperty("EPUB")[0];
            artifacts.Add(new SourceArtifact(file.GetProperty("file").GetProperty("url").GetString()!, "", lang, file.GetProperty("title").GetString()!));
        }
        return artifacts;
    }

    public async Task<SourceFetchResult> FetchAsync(SourceArtifact artifact, long offset, CancellationToken cancellationToken)
    {
        if (artifact.Language is not ("E" or "S")) throw new ArgumentException("NWT language must be E or S.");
        Directory.CreateDirectory(_rawDirectory);
        var path = Path.Combine(_rawDirectory, $"nwt_{artifact.Language}.epub");
        var sourceMarker = path + ".source";
        if (Uri.TryCreate(artifact.Uri, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeFile) path = uri.LocalPath;
        else if (File.Exists(artifact.Uri)) path = Path.GetFullPath(artifact.Uri);
        else if (!File.Exists(path) || !File.Exists(sourceMarker) || File.ReadAllText(sourceMarker) != artifact.Uri)
        {
            var partial = path + ".part";
            var partialSource = partial + ".source";
            if (File.Exists(partial) && (!File.Exists(partialSource) || File.ReadAllText(partialSource) != artifact.Uri)) File.Delete(partial);
            File.WriteAllText(partialSource, artifact.Uri);
            var start = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            using var request = new HttpRequestMessage(HttpMethod.Get, artifact.Uri);
            if (start > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, null);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.StatusCode != HttpStatusCode.PartialContent) start = 0;
            await using (var target = new FileStream(partial, start == 0 ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.None))
                await response.Content.CopyToAsync(target, cancellationToken);
            File.Move(partial, path, true);
            File.Delete(partialSource);
            File.WriteAllText(sourceMarker, artifact.Uri);
        }
        if (offset < 0 || offset > new FileInfo(path).Length) throw new ArgumentOutOfRangeException(nameof(offset));
        await using var hashStream = File.OpenRead(path);
        var sha = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, cancellationToken)).ToLowerInvariant();
        if (artifact.Sha256.Length > 0 && !sha.Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("EPUB SHA-256 mismatch.");
        var stream = File.OpenRead(path);
        stream.Position = offset;
        var provenance = File.Exists(path + ".source") ? File.ReadAllText(path + ".source").Trim() : artifact.Uri;
        return new SourceFetchResult(stream, artifact with { Uri = provenance, Sha256 = sha });
    }

    public Task<ParseResult> ParseAsync(SourceFetchResult source, CancellationToken cancellationToken)
    {
        using var archive = new ZipArchive(source.Content, ZipArchiveMode.Read, leaveOpen: true);
        XDocument Read(string name)
        {
            using var stream = (archive.GetEntry(name) ?? throw new InvalidDataException($"Missing EPUB entry {name}")).Open();
            return XDocument.Load(stream);
        }
        var container = Read("META-INF/container.xml");
        var opfPath = container.Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile")?.Attribute("full-path")?.Value ?? throw new InvalidDataException("Missing OPF rootfile.");
        var root = Path.GetDirectoryName(opfPath)!.Replace('\\', '/');
        string Entry(string relative) => root + "/" + relative;
        var opf = Read(opfPath);
        var manifest = opf.Descendants(Opf + "manifest").Elements(Opf + "item").ToDictionary(e => (string)e.Attribute("id")!, e => (string)e.Attribute("href")!);
        var spine = opf.Descendants(Opf + "spine").Elements(Opf + "itemref").Select(e => manifest[(string)e.Attribute("idref")!]).ToHashSet();
        if (!spine.Contains("biblebooknav.xhtml")) throw new InvalidDataException("Bible navigation absent from OPF spine.");
        var bookLinks = Read(Entry("biblebooknav.xhtml")).Descendants().Where(e => e.Name.LocalName == "a")
            .Select(e => (string?)e.Attribute("href")).Where(h => h is not null && h.EndsWith(".xhtml") && h != "biblebooknav.xhtml").ToArray();
        if (bookLinks.Length != 66) throw new InvalidDataException($"Expected 66 books, found {bookLinks.Length}.");
        var tocLinks = Read(Entry("toc.xhtml")).Descendants().Where(e => e.Name.LocalName == "a").Select(e => (string?)e.Attribute("href")).ToArray();
        var verses = new List<Verse>();
        var sections = new List<Pericope>();
        var expected = new List<string>();
        var omitted = new List<string>();
        var chapterCount = 0;
        for (var book = 1; book <= 66; book++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var navHref = $"biblechapternav{book}.xhtml";
            var bookHref = bookLinks[book - 1]!;
            var singleChapter = bookHref != navHref;
            var nav = Read(Entry(bookHref));
            var name = Clean(nav.Descendants().FirstOrDefault(e => e.Name.LocalName == "title")?.Value ?? "");
            (string? Href, string Number)[] links = singleChapter ? new[] { ((string?)bookHref, "1") } : nav.Descendants().Where(e => e.Name.LocalName == "a")
                .Select(e => (Href: (string?)e.Attribute("href"), Number: Clean(e.Value)))
                .Where(x => x.Href is not null && Regex.IsMatch(x.Href, @"\.xhtml$") && int.TryParse(x.Number, out _)).ToArray();
            if (links.Length == 0) throw new InvalidDataException($"No chapters in book {book}.");
            for (var chapter = 1; chapter <= links.Length; chapter++)
            {
                if (links[chapter - 1].Number != chapter.ToString()) throw new InvalidDataException($"Missing chapter {book}:{chapter}.");
                var href = links[chapter - 1].Href!;
                if (!spine.Contains(href)) throw new InvalidDataException($"Chapter {book}:{chapter} absent from OPF spine.");
                var extracted = ParseChapter(Read(Entry(href)), book, chapter, name, href, omitted);
                var verseNav = Read(Entry($"bibleversenav{book}_{chapter}.xhtml"));
                var inventory = verseNav.Descendants().Where(e => e.Name.LocalName == "a").Select(e => (string?)e.Attribute("href"))
                    .Where(h => h is not null && Regex.IsMatch(h, $@"#chapter{chapter}_verse\d+$"))
                    .Select(h => int.Parse(Regex.Match(h!, @"_verse(\d+)$").Groups[1].Value)).ToArray();
                var actualInventory = inventory.Where(n => !omitted.Contains(BibleVerseId.Format(book, chapter, n))).ToArray();
                if (inventory.Length == 0 || inventory.Distinct().Count() != inventory.Length || !extracted.Select(v => v.Number).SequenceEqual(actualInventory))
                    throw new InvalidDataException($"Verse inventory mismatch at {book}:{chapter}; navigation={string.Join(',', inventory)} extracted={string.Join(',', extracted.Select(v => v.Number))}.");
                expected.AddRange(actualInventory.Select(n => BibleVerseId.Format(book, chapter, n)));
                verses.AddRange(extracted);
                chapterCount++;
            }
            var tocIndex = Array.IndexOf(tocLinks, bookHref);
            var outlineHref = $"{1001061300 + book - 1}.xhtml";
            if (tocIndex < 1 || tocLinks[tocIndex - 1] != outlineHref || !spine.Contains(outlineHref))
                throw new InvalidDataException($"Missing source outline for book {book}.");
            sections.AddRange(ParseOutline(Read(Entry(outlineHref)), book, verses.Where(v => v.Book == book).ToArray()));
        }
        if (chapterCount != 1189) throw new InvalidDataException($"Expected 1,189 chapters, found {chapterCount}.");
        return Task.FromResult(new ParseResult(verses, sections, Array.Empty<string>(), expected));
    }

    private static List<Verse> ParseChapter(XDocument doc, int book, int chapter, string name, string href, List<string> omitted)
    {
        var body = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "body") ?? throw new InvalidDataException($"Missing body in {href}.");
        var result = new List<Verse>();
        var text = new System.Text.StringBuilder();
        var number = 0;
        var hadNoteref = false;
        void Finish()
        {
            if (number == 0) return;
            var value = Clean(text.ToString());
            if (value.Length == 0)
            {
                if (!hadNoteref) throw new InvalidDataException($"Empty verse {book}:{chapter}:{number} in {href}.");
                omitted.Add(BibleVerseId.Format(book, chapter, number));
                text.Clear();
                return;
            }
            result.Add(new Verse(book, chapter, number, value, name, BibleVerseId.Format(book, chapter, number)));
            text.Clear();
        }
        void Walk(XNode node)
        {
            if (node is XText t) { if (number > 0) text.Append(t.Value); return; }
            if (node is not XElement e) return;
            var tag = e.Name.LocalName;
            var cls = (string?)e.Attribute("class") ?? "";
            var epubType = e.Attributes().FirstOrDefault(a => a.Name.LocalName == "type")?.Value ?? "";
            if (epubType.Contains("noteref")) { hadNoteref = true; return; }
            if (tag is "header" or "aside" or "script" or "style" or "img" or "figure" or "sup" || cls.Contains("groupFootnote") || cls.Contains("w_navigation") || cls.Contains("pageNum") || epubType.Contains("footnote")) return;
            if (tag is "h2" or "h3" or "h4")
            {
                var newHeading = Clean(e.Value);
                if (newHeading.Length > 0)
                {
                    if (number > 0) Finish();
                }
                return;
            }
            var marker = VerseMarker.Match((string?)e.Attribute("id") ?? "");
            if (marker.Success)
            {
                if (int.Parse(marker.Groups[1].Value) != chapter || int.Parse(marker.Groups[2].Value) <= number) throw new InvalidDataException($"Malformed verse marker in {href}.");
                Finish(); number = int.Parse(marker.Groups[2].Value); hadNoteref = false;
            }
            foreach (var child in e.Nodes()) Walk(child);
            if (tag is "p" or "div" or "br") text.Append(' ');
        }
        foreach (var node in body.Nodes()) Walk(node);
        Finish();
        return result;
    }

    private static IReadOnlyList<Pericope> ParseOutline(XDocument outline, int book, IReadOnlyList<Verse> verses)
    {
        var result = new List<Pericope>();
        var chapters = outline.Descendants().Where(e => e.Name.LocalName == "li" && ((string?)e.Attribute("class"))?.Contains("chapterNo") == true).ToArray();
        var chapterGroups = verses.GroupBy(v => v.Chapter).ToArray();
        void AddEntry(XElement entry, int chapter, Verse[] chapterVerses)
        {
            var paragraph = entry.Elements().FirstOrDefault(e => e.Name.LocalName == "p") ?? throw new InvalidDataException($"Malformed outline at {book}:{chapter}.");
            var range = paragraph.Descendants().Where(e => e.Name.LocalName == "a").LastOrDefault()?.Value ?? "";
            var numbers = Regex.Matches(range, @"\d+").Select(m => int.Parse(m.Value)).ToArray();
            var heading = Clean(string.Concat(paragraph.Nodes().TakeWhile(n => n is not XElement x || (string?)x.Attribute("class") != "altsize")
                .Select(n => n is XText t ? t.Value : n is XElement x && (string?)x.Attribute("class") == "pageNum" ? "" : n is XElement element ? element.Value : "")));
            if (heading.Length == 0) throw new InvalidDataException($"Malformed outline section at {book}:{chapter}.");
            var start = numbers.Length == 0 ? chapterVerses.Min(v => v.Number) : numbers[0];
            var end = numbers.Length == 0 ? chapterVerses.Max(v => v.Number) : numbers[^1];
            if (start > end || !chapterVerses.Any(v => v.Number >= start && v.Number <= end))
                throw new InvalidDataException($"Invalid outline range at {book}:{chapter}: {range}.");
            result.Add(new Pericope(book, chapter, start, end, heading));
        }
        if (chapters.Length == 0 && chapterGroups.Length == 1)
        {
            var entries = outline.Descendants().Where(e => e.Name.LocalName == "ul" && (string?)e.Attribute("class") == "outline")
                .Elements().Where(e => e.Name.LocalName == "li" && (string?)e.Attribute("class") == "L1").ToArray();
            if (entries.Length == 0) throw new InvalidDataException($"No outline sections in book {book}.");
            foreach (var entry in entries) AddEntry(entry, 1, chapterGroups[0].ToArray());
            return result;
        }
        if (chapters.Length < chapterGroups.Length) throw new InvalidDataException($"Outline chapter count mismatch in book {book}.");
        foreach (var chapterNode in chapters)
        {
            var chapterLabel = Clean(chapterNode.Elements().First(e => e.Name.LocalName == "p").Value);
            if (!int.TryParse(chapterLabel, out var chapter) || chapter < 1 || chapter > chapterGroups.Length)
                throw new InvalidDataException($"Outline chapter mismatch in book {book}: {chapterLabel}.");
            var chapterVerses = chapterGroups[chapter - 1].ToArray();
            var level = ((string?)chapterNode.Attribute("class"))![1] - '0' + 1;
            var entries = chapterNode.Elements().Where(e => e.Name.LocalName == "ul").Elements()
                .Where(e => e.Name.LocalName == "li" && ((string?)e.Attribute("class"))?.StartsWith($"L{level}") == true).ToArray();
            foreach (var entry in entries) AddEntry(entry, chapter, chapterVerses);
            if (entries.Length == 0)
                result.Add(new Pericope(book, chapter, chapterVerses.Min(v => v.Number), chapterVerses.Max(v => v.Number), chapterVerses[0].BookName));
        }
        if (result.Select(p => p.Chapter).Distinct().Count() != chapterGroups.Length)
            throw new InvalidDataException($"Outline omits a chapter in book {book}.");
        return result;
    }

    private static string Clean(string value) => Regex.Replace(value.Replace('\u00a0', ' ').Replace('\u202f', ' '), @"\s+", " ").Trim();

    public static (IReadOnlyList<Verse> Verses, IReadOnlyList<string> Omitted) ParseChapterXhtml(string xhtml, int book, int chapter, string bookName)
    {
        var omitted = new List<string>();
        return (ParseChapter(XDocument.Parse(xhtml), book, chapter, bookName, "fixture.xhtml", omitted), omitted);
    }

    public static IReadOnlyList<Pericope> ParseOutlineXhtml(string xhtml, int book, IReadOnlyList<Verse> verses)
        => ParseOutline(XDocument.Parse(xhtml), book, verses);

    public static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "CorpusLens.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate CorpusLens.sln.");
    }
}

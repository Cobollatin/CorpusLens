using System.Threading.RateLimiting;
using CorpusLens.Core;
using CorpusLens.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCorpusInfrastructure(builder.Configuration);
var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddPolicy("frontend", policy => policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ =>
            new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
app.UseCors("frontend");
app.UseRateLimiter();
app.MapGet("/api/v1/health", (ISearchCorpus corpus) => Results.Ok(new { status = "ok", phase = 5, coverage = corpus.CoverageStatus, embeddingsPopulated = corpus.EmbeddingsPopulated, phase6BackfillPending = !corpus.EmbeddingsPopulated }));
app.MapGet("/api/v1/corpora", (ISearchCorpus corpus) => Results.Ok(new[] { new { language = "E", edition = "English NWT", available = corpus.Verses.Keys.Any(k => k.StartsWith("E:")), embeddingsPopulated = corpus.EmbeddingsPopulated, phase6BackfillPending = !corpus.EmbeddingsPopulated }, new { language = "S", edition = "Español TNM", available = corpus.Verses.Keys.Any(k => k.StartsWith("S:")), embeddingsPopulated = corpus.EmbeddingsPopulated, phase6BackfillPending = !corpus.EmbeddingsPopulated } }));
app.MapGet("/api/v1/chapters/{lang}/{book:int}/{chapter:int}", (string lang, int book, int chapter, ISearchCorpus corpus) =>
{
    if (lang is not ("E" or "S") || book is < 1 or > 66 || chapter is < 1 or > 150) return Results.BadRequest(new { error = "Invalid chapter reference." });
    var verses = corpus.Verses.Where(pair => pair.Key.StartsWith(lang + ":") && pair.Value.Book == book && pair.Value.Chapter == chapter)
        .Select(pair => pair.Value).OrderBy(v => v.Number).Select(v => { var links = BibleLinks.ForVerse(v.Book, v.Chapter, v.Number, lang); return new { id = v.SpanId, number = v.Number, text = v.Text, bookName = v.BookName, jwLink = links.Jw, wolLink = links.Wol }; }).ToArray();
    return verses.Length == 0 ? Results.NotFound() : Results.Ok(new { language = lang, book, chapter, verses });
});
app.MapGet("/api/v1/citations", (string query, CitationQueryParser parser) => Results.Ok(new { citation = parser.Parse(query) }));
app.MapPost("/api/v1/search", async (SearchQuery request, HybridSearchService search, CancellationToken token) =>
{
    try { return Results.Ok(await search.SearchAsync(request, token)); }
    catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
});
app.Run();
public partial class Program { }

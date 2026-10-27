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
app.MapGet("/api/v1/health", (ISearchCorpus corpus) => Results.Ok(new { status = "ok", phase = 4, coverage = corpus.CoverageStatus }));
app.MapGet("/api/v1/corpora", (ISearchCorpus corpus) => Results.Ok(new[] { new { language = "E", edition = "English NWT", available = corpus.Verses.ContainsKey("E:43003016") }, new { language = "S", edition = "Español TNM", available = corpus.Verses.ContainsKey("S:43003016") } }));
app.MapGet("/api/v1/citations", (string query, CitationQueryParser parser) => Results.Ok(new { citation = parser.Parse(query) }));
app.MapPost("/api/v1/search", async (SearchQuery request, HybridSearchService search, CancellationToken token) =>
{
    try { return Results.Ok(await search.SearchAsync(request, token)); }
    catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
});
app.Run();
public partial class Program { }

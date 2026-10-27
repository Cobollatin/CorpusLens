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
app.MapGet("/api/v1/health", () => Results.Ok(new { status = "ok", phase = 1 }));
app.MapGet("/api/v1/corpora", () => Results.Ok(Array.Empty<CorpusSource>()));
app.MapPost("/api/v1/search", (SearchRequest request, QueryNormalizer normalizer) =>
{
    try { _ = normalizer.Normalize(request.Query, request.Language); }
    catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
    return Results.Problem("Search indexing and Oracle retrieval are scheduled for later phases.", statusCode: 503);
});
app.Run();

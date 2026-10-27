using CorpusLens.Infrastructure;
using CorpusLens.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddCorpusInfrastructure(builder.Configuration);
builder.Services.AddHostedService<IngestionWorker>();
builder.Services.AddHostedService<BatchPollingWorker>();
builder.Services.AddHostedService<CoverageGateWorker>();
builder.Services.AddHostedService<AdbKeepAliveWorker>();
builder.Build().Run();

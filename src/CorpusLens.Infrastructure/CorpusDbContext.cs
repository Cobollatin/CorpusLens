using CorpusLens.Core;
using Microsoft.EntityFrameworkCore;

namespace CorpusLens.Infrastructure;

public sealed class CorpusDbContext(DbContextOptions<CorpusDbContext> options) : DbContext(options)
{
    public DbSet<CorpusSource> CorpusSources => Set<CorpusSource>();
    public DbSet<SourceRevision> SourceRevisions => Set<SourceRevision>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentNode> DocumentNodes => Set<DocumentNode>();
    public DbSet<TextSpan> TextSpans => Set<TextSpan>();
    public DbSet<EmbeddingModel> EmbeddingModels => Set<EmbeddingModel>();
    public DbSet<IngestJob> IngestJobs => Set<IngestJob>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CorpusSource>().HasKey(x => x.Id);
        modelBuilder.Entity<SourceRevision>().HasKey(x => x.Id);
        modelBuilder.Entity<Document>().HasKey(x => x.Id);
        modelBuilder.Entity<DocumentNode>().HasKey(x => x.Id);
        modelBuilder.Entity<TextSpan>().HasKey(x => x.Id);
        modelBuilder.Entity<EmbeddingModel>().HasKey(x => x.Id);
        modelBuilder.Entity<IngestJob>().HasKey(x => x.Id);
    }
}

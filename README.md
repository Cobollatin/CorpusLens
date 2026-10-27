# CorpusLens

Phase 1 scaffold for a source-neutral multilingual corpus search system. The locked baseline is Angular, ASP.NET Core, Oracle ADB 23ai, and OpenAI `text-embedding-3-large` at 1,024 dimensions. See [architecture](docs/ARCHITECTURE.md).

## Local checks

```text
dotnet build CorpusLens.sln
dotnet test CorpusLens.sln
cd web && npm ci && npm run build
```

The API exposes `/api/v1/health`, `/api/v1/corpora`, and a validation-only `/api/v1/search` placeholder. EPUB parsing, indexing, Oracle retrieval, and deployment are later phases.

Runtime configuration uses `ConnectionStrings__Oracle`, `TNS_ADMIN` (wallet directory outside this repository), `OpenAI__ApiKey`, and `Cors__AllowedOrigins__0` (exact frontend origin). No live credentials are required for builds or tests. Do not commit EPUBs, wallets, snapshots, or secrets.

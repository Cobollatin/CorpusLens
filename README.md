# CorpusLens

CorpusLens is a source-neutral multilingual corpus search system. Phase 2 extracts and audits official English and Spanish NWT EPUBs. The locked baseline is Angular, ASP.NET Core, Oracle ADB 23ai, and OpenAI `text-embedding-3-large` at 1,024 dimensions. See [architecture](docs/ARCHITECTURE.md).

## Local checks

```text
dotnet build CorpusLens.sln
dotnet test CorpusLens.sln
cd web && npm ci && npm run build
```

## NWT snapshots

```text
dotnet run --project src/CorpusLens.Worker -- snapshot --lang E --lang S
dotnet run --project src/CorpusLens.Worker -- snapshot --lang E --epub C:\path\to\nwt_E.epub
```

The worker uses the official JW CDN publication API for discovery and downloads one cached EPUB per language into `data/raw/`. Interrupted downloads resume from a `.part` file when the server supports byte ranges. It parses the EPUB package spine, book and chapter navigation, verse navigation, and book outlines. It writes ignored `data/snapshots/nwt_{E,S}.snapshot.json` files and the committed [audit manifest](data/manifests/nwt-audit-manifest.json). The manifest records the EPUB → text spans → snapshot SHA-256 chain, source inventory, layer counts, coverage, and verse omissions. Both snapshots are read back and audited before the manifest is written.

The 2026-10-27 source EPUBs yield 31,078 English and 31,062 Spanish nonempty verses, compared with the conventional 31,102 planning count. The NWT source navigation omits John 8:1–11 in both editions. Spanish additionally has note-only markers for 16 verses; they are listed explicitly in the manifest and excluded from the nonempty verse inventory. The remaining conventional count difference reflects edition versification and is not filled with fabricated text. Section chunks come from the EPUB's book outlines, with context headers stored separately from the verse text. The owner's public-display and excerpt rights assessment remains pending.

The API exposes `/api/v1/health`, `/api/v1/corpora`, and a validation-only `/api/v1/search` placeholder. Indexing, Oracle retrieval, and deployment are later phases.

Runtime configuration uses `ConnectionStrings__Oracle`, `TNS_ADMIN` (wallet directory outside this repository), `OpenAI__ApiKey`, and `Cors__AllowedOrigins__0` (exact frontend origin). No live credentials are required for builds or tests. Do not commit EPUBs, wallets, snapshots, or secrets.

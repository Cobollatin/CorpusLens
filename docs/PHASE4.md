# Phase 4 search

The API can search audited local English and Spanish snapshots without a database. Keep `data/snapshots/` outside git.

`/api/v1/search` accepts `query`, `language` (`E`, `S`, `All`), optional `edition`, `limit`, and `mode` (`Hybrid`, `Semantic`, `Lexical`). `/api/v1/citations?query=John%203%3A16` returns a parsed citation. `/api/v1/corpora` and `/api/v1/health` report availability and coverage. IP rate limiting is 60 requests per minute. Configure exact CORS origins with `Cors:AllowedOrigins`.

Live embeddings are disabled unless `OpenAI:EnableLiveEmbeddings=true` and `OpenAI:ApiKey` are supplied at runtime. The query cache uses an in-process LRU and, with Oracle configured, `query_embedding_cache`. The spend ledger lives in ignored `data/spend/` and reserves a conservative estimate before a provider call. Daily and monthly caps are $0.50 and $2.00. A cache miss or guard rejection falls back to lexical search. The local snapshot index supports injected vectors for in-memory cosine retrieval; the snapshots supplied for this phase contain no vectors. The default reranker is a no-op; `IOnnxCrossEncoder` is the bounded local integration hook.

Set `CORPUSLENS_API_BASE_URL` to an HTTPS URL ending in `/api/v1` during the Angular build for Vercel. The build writes public `config.js`. The default `/api/v1` works when the SPA and API share an origin or a proxy.

Oracle retrieval and cache SQL are implemented but were not connected to a live database in this phase. Verify Oracle Text ranking, vector serialization, and query plans against a staged audited revision before deployment. No OCI resources, Oracle connections, or OpenAI calls were made for this implementation.

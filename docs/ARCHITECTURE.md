# CorpusLens architecture plan

**Status:** Planning only. This plan authorizes no repository creation, source acquisition, cloud provisioning, database startup, deployment, SSH access, or copying of keys, wallets, or OCIDs.

**Scope:** A source-neutral multilingual search system, initially populated from the owner's English and Spanish NWT EPUBs. The operating target is under USD 100 over 360 days, subject to the exclusions and free-tier conditions in section 7.

## Executive decision

Build an **Angular SPA on Vercel Hobby**, an **ASP.NET Core Web API plus ingestion and keep-alive workers on OCI A1 Flex in `us-ashburn-1`**, and an **Oracle Autonomous Database 23ai Always Free** primary store. Use **OpenAI `text-embedding-3-large` with `dimensions=1024`** for indexed text and live queries. Index offline through the OpenAI Batch API; embed live cache misses through the standard API. Retrieve bounded Oracle Text and vector candidates and fuse their ranks in SQL.

Phase 5 serves audited text with no embeddings or OpenAI key; Phase 6 adds Batch backfill and vector search. The first source is `NwtEpubCorpusAdapter`, producing a versioned `corpus-snapshot.json` per language. Four layers are locked: atomic verses, 3-verse windows at step 1, 5-verse windows at step 2, and source-defined pericope sections with context headers. Stable `BBCCCVVV` identity, full text coverage, checksums, and per-verse JW.org/WOL links are release gates. Never activate a partial index.

[Vercel Hobby](https://vercel.com/legal/terms) is free for **personal, non-commercial use**. OCI A1 costs $0 only if the tenancy's actual allowance and billing reconcile. A custom domain purchased on Vercel has a separate, variable cost. Azure is discarded; section 9 retains a short decision log.

## 1. Existing repository inventory

The read-only review of [jw-semantic-searcher](https://github.com/Cobollatin/jw-semantic-searcher) and [bible-semantic-searcher](https://github.com/Cobollatin/bible-semantic-searcher) on 2026-10-27 identified useful Angular and .NET patterns. The first uses Angular, Azure Functions, Azure AI Search, and an indexer whose skip paths can leave an incomplete replacement index. The second uses Angular, ASP.NET Core, PostgreSQL, pgvector, and a Spanish FTS index, but its active search is dense-only and its CORS is permissive. Both are Bible-shaped prototypes. Reuse ideas selectively; do not import existing vectors, schemas, or deployment choices. Existing vectors differ in model, dimensions, preprocessing, and granularity.

## 2. Target architecture

```text
Owner's NWT EPUBs -> NwtEpubCorpusAdapter -> corpus-snapshot.json per language
  -> canonical nodes/spans -> four deterministic layers -> coverage audit
  -> OpenAI Batch API (.jsonl, /v1/embeddings, dimensions=1024)
  -> staged ADB revision -> validation -> atomic active-revision switch

Browser -> Angular SPA on Vercel CDN -> https://api.<domain>
  -> Caddy on OCI A1 -> ASP.NET Core Web API
  -> query normalization and guards -> LRU/DB vector cache
  -> OpenAI standard embeddings API on cache miss
  -> ADB Oracle Text + vector candidates -> SQL RRF -> cited results

OCI A1 worker -> daily authenticated SQL keep-alive and alert for ADB
```

The Angular SPA uses Vercel's edge CDN, custom domain purchased on Vercel, and automatic HTTPS. Add the `api.<domain>` DNS record in Vercel DNS. Caddy obtains a Let's Encrypt certificate and proxies to the ASP.NET Core API on OCI A1 Flex in `us-ashburn-1`. Expose only intended HTTP/HTTPS entry points. API CORS permits the exact Vercel production origin only; any preview origin requires a deliberate separate allowlist entry. CORS does not replace server-side validation or rate limits. Cloudflare Tunnel is an alternative ingress if direct public routing is unsuitable. An E2 Micro is optional operations capacity, not part of the required topology.

The API handles filters, cache lookups, query embedding misses, search, citations, rate limits, spend guards, and health. The ingestion worker extracts, audits, and publishes lexical revisions in Phase 5. Batch jobs and vector activation follow in Phase 6. The A1 keep-alive worker executes and records a real read-only SQL command daily. ADB connections use an mTLS wallet held **outside the repository**, with runtime secret injection and restricted file permissions.

## 3. Canonical model and EPUB adapter

Keep generic logical entities: `corpus_source`, `source_revision`, `document`, `document_node`, `text_span`, `search_chunk`, `chunk_segment`, `embedding_model`, `chunk_embedding`, `ingest_job`, `coverage_audit`, and `query_embedding_cache`. Generic nodes represent book/chapter/verse, publication/article/paragraph, or other hierarchies. Adapter-specific attributes can use JSON; identity, relations, text, vectors, and audit state are explicit columns. Store full canonical text once, source offsets, language, edition, rights assessment, artifact SHA-256, normalization version, embedding input hash, model/dimensions, and source locator. Stable IDs derive from source/native identity, revision, and transformation versions. Stage a new revision, audit it, then switch active revision transactionally; retain the previous good revision until safe to retire.

`NwtEpubCorpusAdapter` parses the owner's official `nwt_S.epub` and `nwt_E.epub` offline. Produce a deterministic UTF-8 `corpus-snapshot.json` per edition/language containing source filename/URI and SHA-256, edition/publication, canon, language, attribution, parser and normalization versions, verse records, section boundaries/headings, and per-verse SHA-256. Record the owner's rights assessment; downloadable NWT files do not by themselves grant unrestricted public redistribution. Review public display and excerpt scope before release. The reusable adapter contract has discovery, bounded/resumable fetch, and parse operations with explicit provenance and errors. It does not own embeddings, persistence, or ranking.

Verify the EPUB canon and versification before using 66-book, 1-based `BBCCCVVV`; John 3:16 is `43003016`. Keep this identity separate from edition and locale. Generate verified links per displayed verse, such as `https://www.jw.org/finder?wtlocale=E&bible=43003016&pub=nwtsty` and `https://wol.jw.org/en/wol/b/r1/lp-e/nwtsty/43/3#v=43:3:16`. Validate English and Spanish single-verse and range links before publishing. Use null for unverified mappings. Links are locators, not proof of identical text in another edition.

## 4. Four layers and coverage

| Layer | Granularity | Planning count per language |
|---|---|---:|
| 1 | Atomic verses and citation ground truth | 31,102 for a conventional 66-book canon; verify EPUB |
| 2 | 3-verse sliding windows, step 1, including short tails | ~28,700 |
| 3 | 5-verse sliding windows, step 2, including short tails | ~14,500–16,000 |
| 4 | Source-defined pericope sections with a bounded context header | ~1,000–3,000; count from EPUB |

The planning center is **~76,800 vectors/language** (31,102 + 28,700 + 14,500 + 2,500). Do not cross chapter boundaries; crossing section boundaries requires an explicit versioned policy. Keep raw verse text and citation offsets separate from the embedding context header, which can include edition, book, chapter, section, and range. Enforce the model input limit and record actual tokens.

Coverage requires every eligible canonical text character to belong to at least one indexed segment; every expected verse and source item must be present. Validate Unicode-safe `[start,end)` offsets, exact source substrings, complete interval unions with no gaps, source/verse/chunk hashes, all four layer counts, and Batch input/result IDs. Missing or malformed material fails the revision. Overlap is expected and cannot be audited by concatenating chunks. At 1,024 FLOAT32 dimensions, each vector is 4,096 bytes, or about **0.315 GB raw** for 76,800 vectors. The earlier ~0.8–1.5 GB total/language is an unmeasured allowance for Oracle rows, text, indexes, and overhead; use actual segment and ANN memory measurements before expanding.

## 5. Embeddings, search, and guards

The locked model is [OpenAI `text-embedding-3-large`](https://developers.openai.com/api/docs/models/text-embedding-3-large) with [`dimensions=1024`](https://developers.openai.com/api/docs/guides/embeddings). This uses the model's dimension-shortening design (its default is 3,072). **1,536 dimensions is an optional later measured variant**, requiring a distinct `VECTOR(1536, FLOAT32)` index and full re-embedding. Store model, dimension, normalization, context template, and input checksum; never mix incompatible vectors.

For offline indexing, create idempotent `.jsonl` requests to `/v1/embeddings` with stable custom IDs and `dimensions: 1024`. Submit through the [OpenAI Batch API](https://developers.openai.com/api/docs/guides/batch) with a **24-hour completion window**. Partition within current file/request/embedding-input and queued-token limits; reconcile every result and error before ADB publication. The verified Batch price on 2026-10-27 is **$0.065 per 1M input tokens**. Four layers are estimated at **8.6M–10M tokens/language**, including context headers, costing **$0.559–$0.650 per complete language index**. Recount actual tokenizer inputs; these are estimates, not billed observations.

For live queries, normalize Unicode/whitespace and language-aware case under a versioned policy, cap length, and reject empty text. Key an in-process LRU and DB query-vector cache by normalized query hash, model, dimensions, language, and normalization version; set TTL/eviction and privacy-aware retention. On a miss, use the standard embeddings API at the verified **$0.13 per 1M input tokens**. At 25 tokens/query, **100,000 uncached queries cost $0.325 (~$0.33)** and 1 million cost **$3.25**. Cache hits lower cost; longer queries raise it. Apply IP rate limits, server-side query limits, alerts, actual usage accounting, and an application-side **hard monthly OpenAI spend cutoff** that reserves in-flight budget. Provider alerts or soft budgets alone are insufficient. No paid LLM or hosted reranker is on the request path.

Oracle Text and cosine-vector retrieval each take bounded candidates with identical edition/language/source/access filters. Rank within each branch, union IDs, then compute weighted reciprocal-rank fusion in SQL, for example `SUM(weight/(60+rank))`. Collapse overlapping verse ranges, highlight the best atomic verse, and return complete ranges with per-verse links. Do not directly combine raw Oracle Text scores and vector distances. A bounded local reranker is optional only after measured relevance and latency gains.

After Phase 6, assess relevance and latency with representative citation, lexical, and paraphrase queries. There is no fixed benchmark release gate. A later provider change requires a new decision and full re-index.

## 6. Oracle database

The primary store is **Oracle Autonomous Database 23ai Always Free**, after verifying 23ai availability in the selected home region and pinning the actual version before migrations. Oracle documents up to two 20 GB Always Free databases with fixed compute/session limits. An idle database can stop after seven days; prolonged stopped inactivity can risk reclamation. The A1 worker executes a read-only SQL command daily, logs success, and alerts on failure. A stopped service may require explicit restart; retain recoverable exports and test restore. [Oracle Always Free](https://docs.oracle.com/en/cloud/paas/autonomous-database/serverless/adbsb/autonomous-always-free.html).

Use `VECTOR(1024, FLOAT32)` with model/dimension checks. Start with exact cosine search, then test [HNSW and IVF](https://docs.oracle.com/en/database/oracle/oracle-database/26/sqlrf/create-vector-index.html) for recall, latency, build time, disk, and usable vector-pool memory. HNSW memory may bind before 20 GB disk; IVF is the lower-memory candidate. Create Oracle Text `CONTEXT` with `WORLD_LEXER`, compare language-specific lexers where helpful, and implement RRF in explicit SQL. Match Oracle EF Core/ODP.NET versions for vector mapping; keep Oracle Text and vector-index DDL in explicit migrations. Monitor segments, sessions, index health, query latency, and daily keep-alive.

## 7. Hosting and 360-day cost envelope

Vercel Hobby hosts the Angular SPA at $0 **only while use qualifies as personal/non-commercial** ([pricing](https://vercel.com/pricing), [terms](https://vercel.com/legal/terms)). Its custom domain purchase/renewal is paid and variable, so the table excludes it until a domain is chosen. Vercel supplies edge CDN and automatic HTTPS; Caddy supplies Let's Encrypt TLS for `api.<domain>`. Taxes, paid backups/storage, quota overages, and unusual egress are excluded. Commercial use requires a new hosting cost calculation.

OCI A1 is $0 **only after allowance reconciliation**. The handoff reports a 4 OCPU/24 GB A1 and a recent $0 period, while [Oracle's published Always Free allowance](https://docs.oracle.com/en-us/iaas/Content/FreeTier/freetier_topic-Always_Free_Resources.htm) is 1,500 A1 OCPU-hours and 9,000 GB-hours/month, equivalent to 2 OCPU/12 GB continuously over a 30-day month. Continuous 4/24 usage exceeds that published equivalent. Right-size or revise the budget if actual billing confirms an overage. Optional E2 Micro must likewise remain within its allowance.

Assume 360 days, 25 input tokens per **uncached** live query, four-layer indexing at 8.6M–10M tokens per language, and the verified 2026-10-27 [OpenAI standard and Batch prices](https://developers.openai.com/api/docs/models/text-embedding-3-large) of $0.13/$0.065 per 1M input tokens. Scenario A has one full language index; scenario B has two languages each fully indexed three times (six language indexes). These are full reindexes, not incremental updates.

| 360-day line item | A: 1 language, 100k queries | B: 2 languages × 3 indexes, 1M queries |
|---|---:|---:|
| Vercel Hobby SPA | $0, conditional on permitted use | $0, same condition |
| OCI A1 API/workers | $0, conditional on allowance | $0, same condition |
| Oracle ADB Always Free | $0, within free limits | $0, within free limits |
| OpenAI Batch indexing | $0.56–$0.65 | $3.35–$3.90 |
| OpenAI standard live queries | $0.33 | $3.25 |
| **Operating total, rounded** | **$0.88–$0.98** | **$6.60–$7.15** |

Unrounded lower totals are $0.559 + $0.325 = **$0.884** and $3.354 + $3.25 = **$6.604**. Add domain purchase/renewal, taxes, and any non-free infrastructure before comparing with the USD 100 ceiling. A hard monthly OpenAI cutoff and free-tier monitoring must enforce the operating budget.

## 8. Release gates

- Verify EPUB rights/display scope, source hashes, canon/versification, edition/locale mappings, and representative JW.org/WOL links.
- Prove complete extraction and four-layer coverage before Phase 5 lexical activation; reconcile Batch input/results and vector model/dimensions before Phase 6 vector activation.
- Confirm ADB 23ai version, measured storage and ANN memory, Oracle Text behavior, daily keep-alive, wallet isolation, and restore path.
- Enforce exact production-origin CORS, input limits, IP rate limits, cache privacy/eviction, monthly hard OpenAI spend cutoff, safe logs, and alerting.
- Check English/Spanish lexical relevance and p50/p95 latency; display full cited ranges and collapse duplicate overlapping hits. Assess semantic relevance after Phase 6.
- Reconcile OCI A1 entitlement and billing and track Vercel/domain/free-tier costs before release.

## 9. Historical decision log

Earlier drafts considered self-hosted PostgreSQL/pgvector, local BGE-M3/E5 embeddings, Voyage and Google embeddings, MySQL HeatWave, and Azure App Service, Container Apps, Azure SQL/Cosmos, Azure PostgreSQL, and Azure AI Search. Those evaluations informed portability, coverage, and cost controls. **They are not active deployment options; Azure is discarded.** The locked baseline is OpenAI 3-large/1,024, Oracle ADB 23ai Always Free, OCI A1, and eligible Vercel Hobby. Reopening a provider or store requires a new quality/cost decision and full re-index.

## 10. Final locked baseline and implementation phases

**Locked baseline:** Angular SPA on Vercel Hobby with a Vercel-purchased custom domain and HTTPS; ASP.NET Core API, ingestion, and ADB keep-alive on OCI `us-ashburn-1` A1 behind Caddy at `api.<domain>`; Oracle ADB 23ai Always Free with `VECTOR(1024, FLOAT32)`, Oracle Text `CONTEXT`/`WORLD_LEXER`, measured HNSW/IVF, and SQL RRF; offline NWT EPUB adapter and four-layer snapshot/index; OpenAI `text-embedding-3-large` 1,024-dimensional Batch indexing and standard live-query embeddings with normalization, LRU/DB cache, IP rate limiting, and hard monthly spend cutoff. Cloudflare Tunnel, E2 Micro, and 1,536 dimensions are optional later variants. Azure remains discarded.

### Phase 1 — Repository initialization and scaffolding

- [ ] Initialize the new repository and scaffold Angular SPA, ASP.NET Core API, worker, shared contracts, configuration, and CI.
- [ ] Define canonical entities, versioned identity/normalization, API contracts, and secret/runtime boundaries.
- [ ] Establish development fixtures without committing secrets.

### Phase 2 — EPUB adapter, snapshot, and coverage audit

- [ ] Implement `NwtEpubCorpusAdapter` for owner-provided English/Spanish EPUBs and deterministic per-language `corpus-snapshot.json`.
- [ ] Validate book/chapter/verse and pericope extraction, `BBCCCVVV`, hashes, attribution, and JW.org/WOL locators.
- [ ] Generate all four layers and prove full text coverage, stable IDs, and no unexplained omissions.

### Phase 3 — ADB schema and audited snapshot staging

- [ ] Verify ADB 23ai/free limits; implement schema, `VECTOR(1024, FLOAT32)`, `CONTEXT`/`WORLD_LEXER`, revision staging, and measured HNSW/IVF selection.
- [ ] Prepare bounded Batch `.jsonl` export tooling for later backfill.
- [ ] Stage only audited revisions; measure storage and test restore.

### Phase 4 — API, search, rerank, caching, and guards

- [ ] Implement normalized queries, exact filters, Oracle Text/vector top-K, SQL RRF, overlap collapse, citations, and full-range responses.
- [ ] Add LRU/DB query-vector cache, standard API embedding misses, IP rate limits, query caps, exact CORS, and hard monthly OpenAI cutoff.
- [ ] Verify lexical relevance and latency on representative queries; enable bounded local reranking only if measured improvement justifies it.

### Phase 5 — Full deployment without embeddings

- [ ] Serve lexical search, citation lookup, and chapter browsing from audited Oracle revisions or local snapshots with an empty `chunk_embedding` table and no OpenAI key.
- [ ] Build the Angular UI with verse links and a Phase 6 semantic-search notice. Configure the Vercel API URL at build time.
- [ ] Reconcile OCI A1 allowance; deploy API/worker behind Caddy with the wallet outside the repo, exact-origin CORS, Vercel DNS/domain/SPA, and `api.<domain>` TLS. Keep daily ADB SQL keep-alive and operational monitoring.

### Phase 6 — OpenAI text-embedding-3-large Batch Backfill & Vector Activation

- [ ] Run `batch-export` for each audited language, then `batch-submit`, `batch-status`, and `batch-import`; reconcile every chunk and provider result before `switchover` to vector-enabled search.
- [ ] Enable live query embeddings and vector retrieval only after complete backfill, index validation, and spend guard checks. Estimated initial two-language Batch cost is about **$0.99 total**, subject to actual token counts and current pricing.

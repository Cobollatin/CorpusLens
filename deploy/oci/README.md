# OCI A1 deployment runbook (Phase 5)

This directory contains deployment artifacts only. Nothing here provisions or deploys automatically during build or test. Phase 5 runs lexical search and chapter browsing with an empty `chunk_embedding` table and no `OPENAI_API_KEY`.

## Prepare

1. Verify that the tenancy has A1 Always Free capacity and that Autonomous Database 23ai Always Free is available in `us-ashburn-1`. Review the owner's public display and excerpt rights before serving the NWT text.
2. Configure OCI CLI profile `DOT`. To provision a fresh database deliberately, run `provision-adb23ai.ps1` with a compartment OCID, administrator and wallet passwords, and a wallet directory **outside this repository**. The script stores its request JSON in the system temp directory and deletes it; the wallet remains outside Git. It does not contain OCIDs or credentials.
3. Generate audited snapshots locally from owner-provided EPUBs: `dotnet run --project src/CorpusLens.Worker -- snapshot --lang E --lang S`. Copy only `nwt_E.snapshot.json` and `nwt_S.snapshot.json` to `/opt/corpuslens/snapshots` on the A1. These files remain outside Git. No seed text is committed; the adapter and source EPUBs make the seed reproducible. Set `Corpus__SnapshotPath` to use another path.
4. Install Docker Engine and Compose on the A1. Put the extracted wallet in `/opt/corpuslens/wallet`. Restrict wallet and `.env` permissions to the deployment account. Create `/opt/corpuslens/.env` from `.env.example` on the A1, filling the exact Vercel origin, domain, external snapshot and wallet paths, and Oracle connection string. Include `Tns_Admin=/run/oracle-wallet;Wallet_Location=/run/oracle-wallet;` in the connection string. The deployment script links it into the Compose directory. Keep `.env` outside Git.
5. Allow inbound TCP 80 and 443 in the OCI security list and host firewall. Restrict SSH to the operator's IP. Add a DNS A record for `api.<domain>` pointing to the A1 public IP. Caddy obtains HTTPS automatically and probes `/api/v1/health`; app CORS uses `Cors__AllowedOrigins__0` from the exact Vercel origin.
6. Build the Angular app for Vercel with `CORPUSLENS_API_BASE_URL=https://api.<domain>/api/v1` at build time. `web/vercel.json` provides SPA rewrites. A preview origin needs its own explicit CORS entry.

## First deployment

Run `deploy-to-a1.ps1 -HostIp <public-ip> -SshKeyPath <local-private-key> -Domain <domain> -LocalSnapshotPath <local-snapshot-dir>`. It transfers a `git archive` of `HEAD` and the ignored snapshots, checks for the preinstalled wallet and `.env`, builds both linux/arm64 images, runs the fresh schema migration with IVF, stages and lexically activates each available audited snapshot, then starts API, Worker, and Caddy. The script does not transfer keys or a wallet. Its schema command is for a **fresh database** and is not rerunnable as an upgrade.

When the schema was already migrated from the operator's PC, pass `-SkipMigration` to run the build, seed, and startup without recreating tables. An application schema user needs `CREATE SESSION`, `CREATE TABLE`, quota on `DATA`, and `EXECUTE ON CTXSYS.CTX_DDL` for the Oracle Text migration.

For a manual first deployment, from `/opt/corpuslens/app/deploy/oci`:

```sh
docker compose -f docker-compose.oci.yml build
docker compose -f docker-compose.oci.yml run --rm worker schema-migrate --ivf
docker compose -f docker-compose.oci.yml run --rm worker revision-stage --lang E
docker compose -f docker-compose.oci.yml run --rm worker revision-activate --lang E
docker compose -f docker-compose.oci.yml run --rm worker revision-stage --lang S
docker compose -f docker-compose.oci.yml run --rm worker revision-activate --lang S
docker compose -f docker-compose.oci.yml up -d
curl -fsS https://api.<domain>/api/v1/health
```

Use `docker compose logs` to inspect API, Worker, and Caddy. The Worker starts without an OpenAI key; Oracle keep-alive is registered only with a configured connection. The API can also run from snapshots alone by leaving the Oracle connection empty. The wallet mount path still needs an existing external directory for Compose. `embeddingsPopulated:false` and `phase6BackfillPending:true` are expected until Phase 6. Phase 6 Batch backfill, vector activation, and a service restart are separate steps; do not place an OpenAI key in `.env` during Phase 5.

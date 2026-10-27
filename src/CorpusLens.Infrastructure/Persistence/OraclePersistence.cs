using System.Data.Common;
using System.Reflection;
using CorpusLens.Core;
using Oracle.ManagedDataAccess.Client;
using Microsoft.Extensions.Configuration;

namespace CorpusLens.Infrastructure.Persistence;

public sealed record OracleDatabaseOptions(string ConnectionString, string WalletPath, string TnsAdmin)
{
    public static OracleDatabaseOptions FromEnvironment()
    {
        var connection = Environment.GetEnvironmentVariable("CORPUSLENS_ORACLE_CONNECTION_STRING")
            ?? throw new InvalidOperationException("CORPUSLENS_ORACLE_CONNECTION_STRING is required.");
        var wallet = Environment.GetEnvironmentVariable("CORPUSLENS_ORACLE_WALLET_PATH")
            ?? throw new InvalidOperationException("CORPUSLENS_ORACLE_WALLET_PATH is required.");
        var tns = Environment.GetEnvironmentVariable("TNS_ADMIN")
            ?? throw new InvalidOperationException("TNS_ADMIN is required.");
        var options = new OracleDatabaseOptions(connection, wallet, tns);
        options.Validate();
        return options;
    }
    public static OracleDatabaseOptions FromConfiguration(IConfiguration config)
    {
        var options = new OracleDatabaseOptions(
            config.GetConnectionString("Oracle") ?? Environment.GetEnvironmentVariable("CORPUSLENS_ORACLE_CONNECTION_STRING") ?? "",
            config["Oracle:WalletPath"] ?? Environment.GetEnvironmentVariable("CORPUSLENS_ORACLE_WALLET_PATH") ?? "",
            config["Oracle:TnsAdmin"] ?? Environment.GetEnvironmentVariable("TNS_ADMIN") ?? "");
        options.Validate();
        return options;
    }
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString) || !Directory.Exists(WalletPath) || !Directory.Exists(TnsAdmin))
            throw new InvalidOperationException("Oracle connection string, wallet directory, and TNS_ADMIN directory must be configured.");
    }
}

public sealed class OracleSchemaRunner(Func<DbConnection> connectionFactory)
{
    public static IReadOnlyList<string> Statements(string script) => script.Split("--#", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    public static string ReadEmbedded(string file)
    {
        var assembly = typeof(OracleSchemaRunner).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("Persistence.Migrations." + file, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    public async Task RunAsync(bool useIvf = false, CancellationToken token = default)
    {
        await using var connection = connectionFactory();
        await connection.OpenAsync(token);
        await RunWithExecutorAsync(async sql =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(token);
            }, useIvf);
    }
    public static async Task RunWithExecutorAsync(Func<string, Task> execute, bool useIvf = false)
    {
        foreach (var file in new[] { "001_schema.sql", "002_text.sql", useIvf ? "003_ivf_fallback.sql" : "003_hnsw.sql" })
            foreach (var sql in Statements(ReadEmbedded(file))) await execute(sql);
    }
}

public sealed class OracleCorpusRepository(Func<DbConnection> connectionFactory)
{
    public async Task RecordIngestJobAsync(string revisionId, string shardName, string state, string? batchId, long estimatedTokens, decimal estimatedCost, CancellationToken token)
    {
        await using var connection = connectionFactory(); await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        if (command is OracleCommand oracle) oracle.BindByName = true;
        command.CommandText = """
            MERGE INTO ingest_job t USING (SELECT :id id FROM DUAL) s ON (t.id=s.id)
            WHEN MATCHED THEN UPDATE SET state=:state,external_batch_id=:batch
            WHEN NOT MATCHED THEN INSERT(id,revision_id,state,external_batch_id,estimated_tokens,estimated_cost_usd)
            VALUES(:id,:revision,:state,:batch,:tokens,:cost)
            """;
        Add(command, "id", Hashes.Sha256(revisionId + ":" + shardName));
        Add(command, "revision", revisionId); Add(command, "state", state);
        Add(command, "batch", batchId ?? (object)DBNull.Value);
        Add(command, "tokens", estimatedTokens); Add(command, "cost", estimatedCost);
        await command.ExecuteNonQueryAsync(token);
    }
    public async Task SetRevisionStatusAsync(string revisionId, string expected, string next, CancellationToken token)
    {
        if (expected is not ("Audited" or "Indexing") || next is not ("Indexing" or "Ready")) throw new ArgumentException("Invalid ingestion state transition.");
        await using var connection = connectionFactory(); await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE source_revision SET status=:next WHERE id=:id AND status=:expected";
        Add(command, "next", next); Add(command, "id", revisionId); Add(command, "expected", expected);
        if (await command.ExecuteNonQueryAsync(token) != 1) throw new InvalidOperationException($"Revision is not {expected}.");
    }
    public async Task StageSnapshotAsync(CorpusSnapshot snapshot, CancellationToken token)
    {
        if (!snapshot.Audit.Passed || snapshot.Verses.Count != snapshot.Spans.Count) throw new InvalidDataException("Snapshot audit must pass before staging.");
        var sourceId = "nwt-" + snapshot.Language;
        var revisionId = snapshot.Audit.RevisionId;
        await using var connection = connectionFactory(); await connection.OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        try
        {
            await Insert("MERGE INTO corpus_source t USING (SELECT :id id FROM DUAL) s ON (t.id=s.id) WHEN NOT MATCHED THEN INSERT(id,name,language,edition,rights_assessment) VALUES(:id,:name,:language,:edition,:rights)",
                ("id", sourceId), ("name", "New World Translation"), ("language", snapshot.Language), ("edition", snapshot.Edition), ("rights", snapshot.RightsAssessment ?? ""));
            await Insert("INSERT INTO source_revision(id,source_id,artifact_sha256,parser_version,normalization_version,status) VALUES(:id,:source,:sha,:parser,:normalization,'Staging')",
                ("id", revisionId), ("source", sourceId), ("sha", snapshot.SourceSha256), ("parser", snapshot.ParserVersion), ("normalization", snapshot.NormalizationVersion));
            foreach (var book in snapshot.Verses.GroupBy(v => v.Book))
            {
                var documentId = revisionId + ":book:" + book.Key;
                await Insert("INSERT INTO document(id,revision_id,native_id,title,language) VALUES(:id,:revision,:native,:title,:language)",
                    ("id", documentId), ("revision", revisionId), ("native", book.Key.ToString()), ("title", book.First().BookName), ("language", snapshot.Language));
                foreach (var chapter in book.GroupBy(v => v.Chapter))
                {
                    var chapterId = revisionId + ":chapter:" + book.Key + ":" + chapter.Key;
                    await Insert("INSERT INTO document_node(id,document_id,parent_id,kind,native_id) VALUES(:id,:document,NULL,'chapter',:native)",
                        ("id", chapterId), ("document", documentId), ("native", chapter.Key.ToString()));
                    foreach (var verse in chapter)
                    {
                        var nodeId = revisionId + ":verse:" + verse.SpanId;
                        await Insert("INSERT INTO document_node(id,document_id,parent_id,kind,native_id) VALUES(:id,:document,:parent,'verse',:native)",
                            ("id", nodeId), ("document", documentId), ("parent", chapterId), ("native", verse.SpanId));
                        await Insert("INSERT INTO text_span(id,node_id,raw_text,sha256,source_locator) VALUES(:id,:node,:raw_value,:sha,:locator)",
                            ("id", revisionId + ":" + verse.SpanId), ("node", nodeId), ("raw_value", verse.Text), ("sha", Hashes.Sha256(verse.Text)), ("locator", BibleLinks.ForVerse(verse.Book, verse.Chapter, verse.Number, snapshot.Language).Jw));
                    }
                }
            }
            var spans = snapshot.Spans.ToDictionary(s => s.Id);
            foreach (var chunk in snapshot.Chunks.DistinctBy(chunk => chunk.Id))
            {
                var raw = string.Join(" ", chunk.Segments.Select(s => spans[s.TextSpanId].Text[s.Start..s.End]));
                await Insert("INSERT INTO search_chunk(id,revision_id,layer,raw_text,embedding_input,input_sha256,context_header) VALUES(:id,:revision,:layer,:raw_value,:input,:sha,:header)",
                    ("id", chunk.Id), ("revision", revisionId), ("layer", chunk.Layer), ("raw_value", raw), ("input", chunk.EmbeddingInput), ("sha", chunk.InputSha256), ("header", chunk.ContextHeader ?? (object)DBNull.Value));
                foreach (var segment in chunk.Segments)
                    await Insert("INSERT INTO chunk_segment(chunk_id,text_span_id,start_offset,end_offset) VALUES(:chunk,:span,:start_offset,:end)",
                        ("chunk", chunk.Id), ("span", revisionId + ":" + segment.TextSpanId), ("start_offset", segment.Start), ("end", segment.End));
            }
            await Insert("INSERT INTO coverage_audit(revision_id,passed,report_json) VALUES(:revision,1,:report)",
                ("revision", revisionId), ("report", System.Text.Json.JsonSerializer.Serialize(snapshot.Audit)));
            await Insert("UPDATE source_revision SET status='Audited' WHERE id=:id", ("id", revisionId));
            await transaction.CommitAsync(token);
        }
        catch { await transaction.RollbackAsync(token); throw; }
        async Task Insert(string sql, params (string Name, object Value)[] values)
        {
            await using var cmd = connection.CreateCommand();
            if (cmd is OracleCommand oracle) oracle.BindByName = true;
            cmd.Transaction = transaction; cmd.CommandText = sql;
            foreach (var value in values) Add(cmd, value.Name, value.Value);
            await cmd.ExecuteNonQueryAsync(token);
        }
    }

    public async Task BulkInsertEmbeddingsAsync(string modelId, IReadOnlyList<ChunkEmbedding> embeddings, CancellationToken token)
    {
        await using var connection = connectionFactory();
        await connection.OpenAsync(token);
        foreach (var batch in embeddings.Chunk(100))
        {
            if (batch.Any(e => e.Vector.Length != 1024 || e.Vector.Any(v => !float.IsFinite(v)))) throw new InvalidDataException("Invalid embedding vector.");
            await using var transaction = await connection.BeginTransactionAsync(token);
            try
            {
                await using var command = connection.CreateCommand();
                if (command is not OracleCommand oracleCommand) throw new InvalidOperationException("Oracle connection required for vector array binding.");
                oracleCommand.BindByName = true;
                oracleCommand.ArrayBindCount = batch.Length;
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO chunk_embedding(chunk_id,model_id,embedding,token_count) VALUES(:chunk,:model,TO_VECTOR(:vector),:tokens)";
                var vectorStrings = batch.Select(e => "[" + string.Join(',', e.Vector.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture))) + "]").ToArray();
                oracleCommand.Parameters.Add("chunk", OracleDbType.Varchar2, batch.Select(e => e.ChunkId).ToArray(), System.Data.ParameterDirection.Input);
                oracleCommand.Parameters.Add("model", OracleDbType.Varchar2, batch.Select(_ => modelId).ToArray(), System.Data.ParameterDirection.Input);
                var vectorParameter = oracleCommand.Parameters.Add("vector", OracleDbType.Varchar2, vectorStrings, System.Data.ParameterDirection.Input);
                vectorParameter.ArrayBindSize = vectorStrings.Select(s => s.Length).ToArray();
                oracleCommand.Parameters.Add("tokens", OracleDbType.Int32, batch.Select(e => e.TokenCount).ToArray(), System.Data.ParameterDirection.Input);
                await command.ExecuteNonQueryAsync(token);
                await transaction.CommitAsync(token);
            }
            catch { await transaction.RollbackAsync(token); throw; }
        }
    }
    internal static void Add(DbCommand command, string name, object value)
    { var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value; command.Parameters.Add(parameter); }
}

public sealed class OracleSearchRepository(Func<DbConnection> connectionFactory, HybridSearchOptions? options = null) : ISearchRepository
{
    public static string EscapeTextQuery(string query)
    {
        var terms = System.Text.RegularExpressions.Regex.Matches(query, @"[\p{L}\p{N}]+")
            .Select(m => "{" + m.Value.Replace("}", "") + "}").Take(32).ToArray();
        return terms.Length == 0 ? "{zzzzzzzz}" : string.Join(" OR ", terms);
    }
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchRequest request, float[]? queryVector, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        if (command is OracleCommand oracleCommand) oracleCommand.BindByName = true;
        // Each branch is independently bounded and ranked under identical active-revision filters.
        command.CommandText = """
            WITH text_ranked AS (
              SELECT c.id, ROW_NUMBER() OVER (ORDER BY SCORE(1) DESC,c.id) rank_no
              FROM search_chunk c JOIN source_revision r ON r.id=c.revision_id JOIN corpus_source s ON s.id=r.source_id
              WHERE r.status='Active' AND s.language=:language AND (:edition IS NULL OR s.edition=:edition) AND CONTAINS(c.raw_text,:query,1)>0
            ), text_hits AS (SELECT id,rank_no FROM text_ranked WHERE rank_no<=50),
            vector_ranked AS (
              SELECT c.id, ROW_NUMBER() OVER (ORDER BY VECTOR_DISTANCE(e.embedding,TO_VECTOR(:vector),COSINE),c.id) rank_no
              FROM search_chunk c JOIN chunk_embedding e ON e.chunk_id=c.id
              JOIN source_revision r ON r.id=c.revision_id JOIN corpus_source s ON s.id=r.source_id
              WHERE r.status='Active' AND s.language=:language AND (:edition IS NULL OR s.edition=:edition) AND :vector IS NOT NULL
            ), vector_hits AS (SELECT id,rank_no FROM vector_ranked WHERE rank_no<=50),
            fused AS (
              SELECT id,SUM(score) fused_score,SUM(CASE WHEN branch='dense' THEN score ELSE 0 END) dense_score,SUM(CASE WHEN branch='lexical' THEN score ELSE 0 END) lexical_score FROM (
                SELECT id,:lexical_weight/(60+rank_no) score,'lexical' branch FROM text_hits UNION ALL
                SELECT id,:vector_weight/(60+rank_no) score,'dense' branch FROM vector_hits
              ) GROUP BY id
            ) SELECT c.id,c.raw_text,CAST(f.fused_score*(CASE WHEN c.layer='SingleVerse' THEN 1+:layer_boost ELSE 1 END) AS BINARY_DOUBLE),c.layer,s.language,CAST(f.dense_score AS BINARY_DOUBLE),CAST(f.lexical_score AS BINARY_DOUBLE)
            FROM fused f JOIN search_chunk c ON c.id=f.id JOIN source_revision r ON r.id=c.revision_id JOIN corpus_source s ON s.id=r.source_id
            ORDER BY 3 DESC FETCH FIRST :limit ROWS ONLY
            """;
        if (queryVector is null)
            command.CommandText = """
                WITH text_ranked AS (
                  SELECT c.id, ROW_NUMBER() OVER (ORDER BY SCORE(1) DESC,c.id) rank_no
                  FROM search_chunk c JOIN source_revision r ON r.id=c.revision_id JOIN corpus_source s ON s.id=r.source_id
                  WHERE r.status='Active' AND s.language=:language AND (:edition IS NULL OR s.edition=:edition) AND CONTAINS(c.raw_text,:query,1)>0
                ), text_hits AS (SELECT id,rank_no FROM text_ranked WHERE rank_no<=50)
                SELECT c.id,c.raw_text,CAST(:lexical_weight/(60+t.rank_no)*(CASE WHEN c.layer='SingleVerse' THEN 1+:layer_boost ELSE 1 END) AS BINARY_DOUBLE),
                  c.layer,s.language,CAST(0 AS BINARY_DOUBLE),CAST(:lexical_weight/(60+t.rank_no) AS BINARY_DOUBLE)
                FROM text_hits t JOIN search_chunk c ON c.id=t.id JOIN source_revision r ON r.id=c.revision_id JOIN corpus_source s ON s.id=r.source_id
                ORDER BY 3 DESC FETCH FIRST :limit ROWS ONLY
                """;
        OracleCorpusRepository.Add(command, "language", request.Language);
        OracleCorpusRepository.Add(command, "edition", request.Edition is null ? DBNull.Value : request.Edition);
        OracleCorpusRepository.Add(command, "query", EscapeTextQuery(request.Query));
        if (queryVector is not null) OracleCorpusRepository.Add(command, "vector", "[" + string.Join(',', queryVector.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture))) + "]");
        OracleCorpusRepository.Add(command, "lexical_weight", (options ?? new()).LexicalWeight);
        if (queryVector is not null) OracleCorpusRepository.Add(command, "vector_weight", (options ?? new()).VectorWeight);
        OracleCorpusRepository.Add(command, "layer_boost", (options ?? new()).LayerBoost);
        OracleCorpusRepository.Add(command, "limit", Math.Clamp(request.Limit, 1, 50));
        var hits = new List<SearchHit>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) hits.Add(new SearchHit(reader.GetString(0), reader.GetString(1), Convert.ToDouble(reader.GetValue(2)), [], reader.GetString(3), reader.GetString(4), Convert.ToDouble(reader.GetValue(5)), Convert.ToDouble(reader.GetValue(6))));
        await reader.DisposeAsync();
        for (var i = 0; i < hits.Count; i++)
        {
            await using var segments = connection.CreateCommand();
            segments.CommandText = "SELECT n.native_id FROM chunk_segment cs JOIN text_span t ON t.id=cs.text_span_id JOIN document_node n ON n.id=t.node_id WHERE cs.chunk_id=:id ORDER BY n.native_id";
            OracleCorpusRepository.Add(segments, "id", hits[i].ChunkId);
            var ids = new List<string>();
            await using var segmentReader = await segments.ExecuteReaderAsync(cancellationToken);
            while (await segmentReader.ReadAsync(cancellationToken)) ids.Add(segmentReader.GetString(0));
            hits[i] = hits[i] with { VerseIds = ids };
        }
        return hits;
    }
}

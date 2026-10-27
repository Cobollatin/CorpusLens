using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using CorpusLens.Core;
using Oracle.ManagedDataAccess.Client;

namespace CorpusLens.Infrastructure.Persistence;

public sealed class OracleQueryEmbeddingCacheRepository(Func<DbConnection> connectionFactory) : IQueryEmbeddingCacheRepository
{
    public async Task<float[]?> GetAsync(string key, CancellationToken token)
    {
        await using var connection = connectionFactory(); await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT VECTOR_SERIALIZE(embedding RETURNING CLOB) FROM query_embedding_cache WHERE cache_key=:key AND expires_at>SYSTIMESTAMP";
        OracleCorpusRepository.Add(command, "key", key);
        var value = await command.ExecuteScalarAsync(token);
        return value is null or DBNull ? null : JsonSerializer.Deserialize<float[]>(Convert.ToString(value, CultureInfo.InvariantCulture)!);
    }
    public async Task PutAsync(string key, string language, float[] vector, CancellationToken token)
    {
        if (vector.Length != 1024 || vector.Any(v => !float.IsFinite(v))) throw new ArgumentException("Invalid query vector.");
        await using var connection = connectionFactory(); await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        if (command is OracleCommand oracle) oracle.BindByName = true;
        command.CommandText = "MERGE INTO query_embedding_cache t USING (SELECT :key cache_key FROM DUAL) s ON (t.cache_key=s.cache_key) WHEN MATCHED THEN UPDATE SET embedding=TO_VECTOR(:vector),expires_at=SYSTIMESTAMP+INTERVAL '7' DAY WHEN NOT MATCHED THEN INSERT(cache_key,model_id,language,embedding,expires_at) VALUES(:key,'text-embedding-3-large-1024',:language,TO_VECTOR(:vector),SYSTIMESTAMP+INTERVAL '7' DAY)";
        OracleCorpusRepository.Add(command, "key", key);
        OracleCorpusRepository.Add(command, "language", language);
        ((OracleCommand)command).Parameters.Add("vector", OracleDbType.Clob).Value = JsonSerializer.Serialize(vector);
        await command.ExecuteNonQueryAsync(token);
    }
}

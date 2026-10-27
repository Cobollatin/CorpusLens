using System.Data.Common;
using CorpusLens.Core;

namespace CorpusLens.Infrastructure.Persistence;

public sealed class OracleSearchCorpus : ISearchCorpus
{
    private readonly Func<DbConnection> _connections;
    private readonly OracleSearchRepository _repository;
    private readonly Lazy<IReadOnlyDictionary<string, Verse>> _verses;
    public OracleSearchCorpus(Func<DbConnection> connections, HybridSearchOptions options)
    {
        _connections = connections;
        _repository = new OracleSearchRepository(connections, options);
        _verses = new Lazy<IReadOnlyDictionary<string, Verse>>(LoadVerses);
        _embeddingsPopulated = new Lazy<bool>(CheckEmbeddings);
    }
    public IReadOnlyDictionary<string, Verse> Verses => _verses.Value;
    public IReadOnlyList<Pericope> Pericopes => [];
    public string CoverageStatus => "Active Oracle revisions";
    public bool EmbeddingsPopulated => _embeddingsPopulated.Value;
    private readonly Lazy<bool> _embeddingsPopulated;
    private IReadOnlyDictionary<string, Verse> LoadVerses()
    {
        using var connection = _connections(); connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT s.language,n.native_id,DBMS_LOB.SUBSTR(t.raw_text,1000,1),d.title FROM text_span t JOIN document_node n ON n.id=t.node_id JOIN document d ON d.id=n.document_id JOIN source_revision r ON r.id=d.revision_id JOIN corpus_source s ON s.id=r.source_id WHERE r.status='Active' AND n.kind='verse'";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, Verse>();
        while (reader.Read())
        {
            var id = reader.GetString(1);
            if (id.Length != 8 || !int.TryParse(id[..2], out var book) || !int.TryParse(id.Substring(2, 3), out var chapter) || !int.TryParse(id[5..], out var number)) continue;
            result[reader.GetString(0) + ":" + id] = new Verse(book, chapter, number, reader.GetString(2), reader.GetString(3), id);
        }
        return result;
    }
    private bool CheckEmbeddings()
    {
        using var connection = _connections(); connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT CASE WHEN COUNT(DISTINCT s.language)=2 AND MIN(CASE WHEN counts.chunks=counts.embeddings AND counts.chunks>0 THEN 1 ELSE 0 END)=1 THEN 1 ELSE 0 END FROM (SELECT r.id,COUNT(c.id) chunks,COUNT(e.chunk_id) embeddings FROM source_revision r JOIN search_chunk c ON c.revision_id=r.id LEFT JOIN chunk_embedding e ON e.chunk_id=c.id WHERE r.status='Active' GROUP BY r.id) counts JOIN source_revision r ON r.id=counts.id JOIN corpus_source s ON s.id=r.source_id";
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }
    public IReadOnlyList<SearchCandidate> Retrieve(string query, string language, string? edition, float[]? vector, string mode)
    {
        var hits = _repository.SearchAsync(new SearchRequest(query, language, edition, 200), mode == "Lexical" ? null : vector, CancellationToken.None).GetAwaiter().GetResult();
        return hits.Select(hit => new SearchCandidate(hit.ChunkId, hit.Layer, hit.VerseIds, null, null, null, null, hit.Language, hit.Score, hit.DenseRrf, hit.LexicalRrf)).ToArray();
    }
}

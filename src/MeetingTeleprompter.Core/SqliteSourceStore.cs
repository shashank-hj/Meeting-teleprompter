using Microsoft.Data.Sqlite;

namespace MeetingTeleprompter.Core;

public sealed partial class SqliteSourceStore : ISourceRepository, IRetriever, IDisposable
{
    private readonly string _connectionString;
    private readonly string? _vecPath;
    public bool HasNativeVectors => !string.IsNullOrWhiteSpace(_vecPath);

    public SqliteSourceStore(string databasePath, string? vecPath = null)
    {
        _vecPath = string.IsNullOrWhiteSpace(vecPath) ? null : Path.GetFullPath(vecPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate, ForeignKeys = true }.ToString();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT sql FROM sqlite_master WHERE name='chunks_fts'";
        var oldSchema = command.ExecuteScalar() as string;
        command.CommandText = "PRAGMA user_version";
        var schemaVersion = Convert.ToInt32(command.ExecuteScalar());
        var migrate = oldSchema?.Contains("content=", StringComparison.OrdinalIgnoreCase) == true;
        if (migrate)
        {
            command.CommandText = "DROP TABLE chunks_fts";
            command.ExecuteNonQuery();
        }
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS sources (
                id TEXT PRIMARY KEY,
                source_type TEXT NOT NULL,
                location TEXT NOT NULL UNIQUE,
                title TEXT NOT NULL,
                fetched_at TEXT NULL,
                content_hash TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS chunks (
                id TEXT PRIMARY KEY,
                source_id TEXT NOT NULL REFERENCES sources(id) ON DELETE CASCADE,
                chunk_index INTEGER NOT NULL,
                page INTEGER NULL,
                heading TEXT NOT NULL,
                text TEXT NOT NULL
            );
            CREATE VIRTUAL TABLE IF NOT EXISTS chunks_fts USING fts5(chunk_id UNINDEXED, text);
            CREATE TRIGGER IF NOT EXISTS chunks_delete AFTER DELETE ON chunks BEGIN
                DELETE FROM chunks_fts WHERE rowid=old.rowid;
            END;
            DROP TRIGGER IF EXISTS chunks_insert;
            DROP TRIGGER IF EXISTS chunks_update;
            CREATE TRIGGER chunks_insert AFTER INSERT ON chunks BEGIN
                INSERT INTO chunks_fts(rowid,chunk_id,text) VALUES(new.rowid,new.id,new.heading || char(10) || new.text);
            END;
            CREATE TRIGGER chunks_update AFTER UPDATE OF text,heading ON chunks BEGIN
                DELETE FROM chunks_fts WHERE rowid=old.rowid;
                INSERT INTO chunks_fts(rowid,chunk_id,text) VALUES(new.rowid,new.id,new.heading || char(10) || new.text);
            END;
            CREATE TABLE IF NOT EXISTS embeddings (
                chunk_id TEXT PRIMARY KEY REFERENCES chunks(id) ON DELETE CASCADE,
                model TEXT NOT NULL, vector BLOB NOT NULL
            );
            """;
        command.ExecuteNonQuery();
        command.CommandText = "PRAGMA table_info(sources)";
        var columns = new HashSet<string>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) columns.Add(reader.GetString(1));
        foreach (var (name, definition) in new[] { ("retrieval_status", "TEXT NOT NULL DEFAULT 'indexed'"), ("last_error", "TEXT NULL"), ("last_attempt_at", "TEXT NULL"), ("parser_version", "INTEGER NOT NULL DEFAULT 0") })
        {
            if (columns.Contains(name)) continue;
            command.CommandText = "ALTER TABLE sources ADD COLUMN " + name + " " + definition;
            command.ExecuteNonQuery();
        }
        if (oldSchema is null || migrate || schemaVersion < 1)
        {
            command.CommandText = "DELETE FROM chunks_fts; INSERT INTO chunks_fts(rowid,chunk_id,text) SELECT rowid,id,heading || char(10) || text FROM chunks; PRAGMA user_version=1";
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public async Task UpsertAsync(SourceDocument document, CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var source = connection.CreateCommand())
        {
            source.Transaction = transaction;
            // Title is part of the embedding input. Text changes invalidate only their own vectors.
            source.CommandText = "DELETE FROM embeddings WHERE chunk_id IN (SELECT c.id FROM chunks c JOIN sources s ON s.id=c.source_id WHERE s.id=$id AND s.title<>$title)";
            source.Parameters.AddWithValue("$id", document.Id);
            source.Parameters.AddWithValue("$title", document.Title);
            await source.ExecuteNonQueryAsync(cancellationToken);
            source.Parameters.Clear();
            source.CommandText = """
                INSERT INTO sources(id,source_type,location,title,fetched_at,content_hash,updated_at,retrieval_status,last_attempt_at,parser_version) VALUES($id,$type,$location,$title,$fetched,$hash,$updated,'indexed',$updated,$parser)
                ON CONFLICT(id) DO UPDATE SET title=excluded.title,fetched_at=excluded.fetched_at,content_hash=excluded.content_hash,updated_at=excluded.updated_at,retrieval_status='indexed',last_error=NULL,last_attempt_at=excluded.last_attempt_at,parser_version=excluded.parser_version
                """;
            source.Parameters.AddWithValue("$id", document.Id);
            source.Parameters.AddWithValue("$type", document.Type);
            source.Parameters.AddWithValue("$location", document.Location);
            source.Parameters.AddWithValue("$title", document.Title);
            source.Parameters.AddWithValue("$fetched", document.Type == "url" ? document.UpdatedAt.ToString("O") : DBNull.Value);
            source.Parameters.AddWithValue("$hash", document.ContentHash);
            source.Parameters.AddWithValue("$updated", document.UpdatedAt.ToString("O"));
            source.Parameters.AddWithValue("$parser", document.ParserVersion);
            await source.ExecuteNonQueryAsync(cancellationToken);
        }
        var retained = document.Chunks.Select(x => x.Id).ToHashSet();
        var oldIds = new List<string>();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT id FROM chunks WHERE source_id=$source";
            read.Parameters.AddWithValue("$source", document.Id);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) oldIds.Add(reader.GetString(0));
        }
        foreach (var id in oldIds.Where(x => !retained.Contains(x)))
        {
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM chunks WHERE id=$id";
            delete.Parameters.AddWithValue("$id", id);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var chunk in document.Chunks)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "DELETE FROM embeddings WHERE chunk_id=$id AND EXISTS(SELECT 1 FROM chunks WHERE id=$id AND text<>$text)";
            insert.Parameters.AddWithValue("$id", chunk.Id);
            insert.Parameters.AddWithValue("$text", chunk.Text);
            await insert.ExecuteNonQueryAsync(cancellationToken);
            insert.Parameters.Clear();
            insert.CommandText = """
                INSERT INTO chunks(id,source_id,chunk_index,page,heading,text) VALUES($id,$source,$index,$page,$heading,$text)
                ON CONFLICT(id) DO UPDATE SET chunk_index=excluded.chunk_index,page=excluded.page,heading=excluded.heading,text=excluded.text
                WHERE chunks.chunk_index<>excluded.chunk_index OR chunks.page IS NOT excluded.page OR chunks.heading<>excluded.heading OR chunks.text<>excluded.text
                """;
            insert.Parameters.AddWithValue("$id", chunk.Id);
            insert.Parameters.AddWithValue("$source", document.Id);
            insert.Parameters.AddWithValue("$index", chunk.Index);
            insert.Parameters.AddWithValue("$page", (object?)chunk.Page ?? DBNull.Value);
            insert.Parameters.AddWithValue("$heading", chunk.Heading);
            insert.Parameters.AddWithValue("$text", chunk.Text);
            await insert.ExecuteNonQueryAsync(cancellationToken);

        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RemoveAsync(string sourceId, CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sources WHERE id = $id";
        command.Parameters.AddWithValue("$id", sourceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SourceDocument>> ListAsync(CancellationToken cancellationToken)
    {
        var result = new List<SourceDocument>();
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,source_type,location,title,fetched_at,content_hash,updated_at,retrieval_status,last_error,last_attempt_at,parser_version FROM sources ORDER BY updated_at DESC";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new SourceDocument(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), DateTimeOffset.Parse(reader.GetString(6)), reader.GetString(5), [],
                reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9)), reader.GetInt32(10)));
        return result;
    }

    public async Task RecordUrlFailureAsync(Uri uri, string error, CancellationToken ct)
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sources(id,source_type,location,title,fetched_at,content_hash,updated_at,retrieval_status,last_error,last_attempt_at)
            VALUES($id,'url',$location,$title,NULL,'',$now,'failed',$error,$now)
            ON CONFLICT(id) DO UPDATE SET retrieval_status='failed',last_error=excluded.last_error,last_attempt_at=excluded.last_attempt_at
            """;
        command.Parameters.AddWithValue("$id", DocumentChunker.Hash(uri.AbsoluteUri));
        command.Parameters.AddWithValue("$location", uri.AbsoluteUri);
        command.Parameters.AddWithValue("$title", uri.Host);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$error", error[..Math.Min(error.Length, 500)]);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<EvidenceChunk>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var terms = SearchTerms.Meaningful(query)
            .Select(x => $"\"{x}\"")
            .ToArray();
        if (terms.Length == 0) return [];
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id,c.text,c.chunk_index,s.id,s.source_type,s.location,s.title,s.fetched_at,s.content_hash,bm25(chunks_fts) AS score,c.page,c.heading
            FROM chunks_fts JOIN chunks c ON c.id = chunks_fts.chunk_id JOIN sources s ON s.id = c.source_id
            WHERE chunks_fts MATCH $query ORDER BY score LIMIT $limit
            """;
        command.Parameters.AddWithValue("$query", string.Join(" OR ", terms));
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 50));
        var result = new List<EvidenceChunk>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var fetched = reader.IsDBNull(7) ? (DateTimeOffset?)null : DateTimeOffset.Parse(reader.GetString(7));
            var source = new SourceReference(reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), fetched, reader.GetString(8), reader.IsDBNull(10) ? null : reader.GetInt32(10), reader.GetInt32(2), reader.GetString(11));
            result.Add(new EvidenceChunk(reader.GetString(0), reader.GetString(1), source, reader.GetDouble(9)));
        }
        return result.Where(x => SearchTerms.Overlaps(query, x.Text + " " + x.Source.Title + " " + x.Source.Heading)).ToArray();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        if (_vecPath is not null)
        {
            connection.EnableExtensions(true);
            connection.LoadExtension(_vecPath);
            connection.EnableExtensions(false);
        }
        return connection;
    }

    public void Dispose() { }
}

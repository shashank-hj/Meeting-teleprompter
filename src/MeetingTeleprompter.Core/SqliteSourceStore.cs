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
            CREATE TRIGGER IF NOT EXISTS chunks_insert AFTER INSERT ON chunks BEGIN
                INSERT INTO chunks_fts(rowid,chunk_id,text) VALUES(new.rowid,new.id,new.text);
            END;
            CREATE TABLE IF NOT EXISTS embeddings (
                chunk_id TEXT PRIMARY KEY REFERENCES chunks(id) ON DELETE CASCADE,
                model TEXT NOT NULL, vector BLOB NOT NULL
            );
            """;
        command.ExecuteNonQuery();
        if (oldSchema is null || migrate)
        {
            command.CommandText = "INSERT INTO chunks_fts(rowid,chunk_id,text) SELECT rowid,id,text FROM chunks";
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public async Task UpsertAsync(SourceDocument document, CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM sources WHERE location = $location";
            delete.Parameters.AddWithValue("$location", document.Location);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var source = connection.CreateCommand())
        {
            source.Transaction = transaction;
            source.CommandText = "INSERT INTO sources(id,source_type,location,title,fetched_at,content_hash,updated_at) VALUES($id,$type,$location,$title,$fetched,$hash,$updated)";
            source.Parameters.AddWithValue("$id", document.Id);
            source.Parameters.AddWithValue("$type", document.Type);
            source.Parameters.AddWithValue("$location", document.Location);
            source.Parameters.AddWithValue("$title", document.Title);
            source.Parameters.AddWithValue("$fetched", document.Type == "url" ? document.UpdatedAt.ToString("O") : DBNull.Value);
            source.Parameters.AddWithValue("$hash", document.ContentHash);
            source.Parameters.AddWithValue("$updated", document.UpdatedAt.ToString("O"));
            await source.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var chunk in document.Chunks)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO chunks(id,source_id,chunk_index,page,heading,text) VALUES($id,$source,$index,$page,$heading,$text)";
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
        command.CommandText = "SELECT id,source_type,location,title,fetched_at,content_hash,updated_at FROM sources ORDER BY updated_at DESC";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new SourceDocument(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), DateTimeOffset.Parse(reader.GetString(6)), reader.GetString(5), []));
        return result;
    }

    public async Task<IReadOnlyList<EvidenceChunk>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.Replace("\"", "", StringComparison.Ordinal))
            .Where(x => x.Length > 1)
            .Select(x => $"\"{x}\"")
            .ToArray();
        if (terms.Length == 0) return [];
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id,c.text,c.chunk_index,s.id,s.source_type,s.location,s.title,s.fetched_at,s.content_hash,bm25(chunks_fts) AS score,c.page
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
            var source = new SourceReference(reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), fetched, reader.GetString(8), reader.IsDBNull(10) ? null : reader.GetInt32(10), reader.GetInt32(2));
            result.Add(new EvidenceChunk(reader.GetString(0), reader.GetString(1), source, reader.GetDouble(9)));
        }
        return result;
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

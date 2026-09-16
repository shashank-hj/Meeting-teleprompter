using System.Text.Json;

namespace MeetingTeleprompter.Core;

public sealed partial class SqliteSourceStore
{
    public async Task<IReadOnlyList<EvidenceChunk>> NativeVectorSearchAsync(float[] vector, string model, int limit, CancellationToken ct)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id,c.text,c.chunk_index,c.page,s.id,s.source_type,s.location,s.title,s.fetched_at,s.content_hash,
                1-vec_distance_cosine(CAST(e.vector AS TEXT),$query) AS similarity
            FROM embeddings e JOIN chunks c ON c.id=e.chunk_id JOIN sources s ON s.id=c.source_id
            WHERE e.model=$model AND similarity > 0.3 ORDER BY similarity DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$query", JsonSerializer.Serialize(vector));
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<EvidenceChunk>();
        while (await reader.ReadAsync(ct))
            result.Add(new(reader.GetString(0), reader.GetString(1), new(reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)), reader.GetString(9), reader.IsDBNull(3) ? null : reader.GetInt32(3), reader.GetInt32(2)), reader.GetDouble(10)));
        return result;
    }
    public async Task<IReadOnlyList<EvidenceChunk>> AllChunksAsync(CancellationToken ct)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT c.id,c.text,c.chunk_index,c.page,s.id,s.source_type,s.location,s.title,s.fetched_at,s.content_hash FROM chunks c JOIN sources s ON s.id=c.source_id";
        var result = new List<EvidenceChunk>();
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new(reader.GetString(0), reader.GetString(1), new(reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)), reader.GetString(9), reader.IsDBNull(3) ? null : reader.GetInt32(3), reader.GetInt32(2)), 0));
        return result;
    }
    public async Task SaveVectorAsync(string id, string model, float[] vector, CancellationToken ct)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO embeddings(chunk_id,model,vector) VALUES($id,$model,$vector)";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$vector", JsonSerializer.SerializeToUtf8Bytes(vector));
        await command.ExecuteNonQueryAsync(ct);
    }
    public async Task<Dictionary<string, float[]>> VectorsAsync(string model, CancellationToken ct)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT chunk_id,vector FROM embeddings WHERE model=$model";
        command.Parameters.AddWithValue("$model", model);
        using var reader = await command.ExecuteReaderAsync(ct);
        var result = new Dictionary<string, float[]>();
        while (await reader.ReadAsync(ct)) result[reader.GetString(0)] = JsonSerializer.Deserialize<float[]>((byte[])reader[1])!;
        return result;
    }
}

public sealed class HybridRetriever(SqliteSourceStore store, IEmbedder embedder, string model) : IRetriever
{
    public async Task IndexMissingAsync(CancellationToken ct)
    {
        var vectors = await store.VectorsAsync(model, ct);
        foreach (var chunk in await store.AllChunksAsync(ct))
        {
            if (vectors.ContainsKey(chunk.Id)) continue;
            var embeddings = await embedder.EmbedAsync(["title: " + chunk.Source.Title + " | text: " + chunk.Text], ct);
            if (embeddings.Length != 1 || embeddings[0].Length == 0) throw new InvalidOperationException("Invalid embedding response.");
            await store.SaveVectorAsync(chunk.Id, model, embeddings[0], ct);
        }
    }
    public async Task<IReadOnlyList<EvidenceChunk>> SearchAsync(string query, int limit, CancellationToken ct)
    {
        var lexical = await store.SearchAsync(query, 20, ct);
        var vector = (await embedder.EmbedAsync(["task: search result | query: " + query], ct))[0];
        if (store.HasNativeVectors) return Fuse(lexical, await store.NativeVectorSearchAsync(vector, model, 20, ct), limit);
        var vectors = await store.VectorsAsync(model, ct);
        var semantic = (await store.AllChunksAsync(ct)).Where(x => vectors.ContainsKey(x.Id))
            .Select(x => x with { Score = Cosine(vector, vectors[x.Id]) }).Where(x => x.Score > .3).OrderByDescending(x => x.Score).Take(20).ToArray();
        return Fuse(lexical, semantic, limit);
    }
    public static IReadOnlyList<EvidenceChunk> Fuse(IReadOnlyList<EvidenceChunk> lexical, IReadOnlyList<EvidenceChunk> semantic, int limit) =>
        lexical.Select((x, i) => x with { Score = 1d / (60 + i + 1) })
            .Concat(semantic.Select((x, i) => x with { Score = 1d / (60 + i + 1) }))
            .GroupBy(x => x.Id).Select(g => g.First() with { Score = g.Sum(x => x.Score) })
            .OrderByDescending(x => x.Score).Take(limit).ToArray();
    private static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        double dot = 0, aa = 0, bb = 0;
        for (var i = 0; i < a.Length; i++) { dot += a[i] * b[i]; aa += a[i] * a[i]; bb += b[i] * b[i]; }
        return aa == 0 || bb == 0 ? 0 : dot / Math.Sqrt(aa * bb);
    }
}

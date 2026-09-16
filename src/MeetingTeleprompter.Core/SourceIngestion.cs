using System.Security.Cryptography;
using System.Text;

namespace MeetingTeleprompter.Core;

public sealed record SourceDocument(
    string Id,
    string Type,
    string Location,
    string Title,
    DateTimeOffset UpdatedAt,
    string ContentHash,
    IReadOnlyList<DocumentChunk> Chunks);

public sealed record DocumentChunk(
    string Id,
    int Index,
    string Text,
    int? Page,
    string Heading);

public interface IDocumentParser
{
    bool CanParse(string extension, string? mediaType);
    Task<string> ExtractTextAsync(Stream content, string? mediaType, CancellationToken cancellationToken);
}

public interface ISourceRepository
{
    Task UpsertAsync(SourceDocument document, CancellationToken cancellationToken);
    Task RemoveAsync(string sourceId, CancellationToken cancellationToken);
    Task<IReadOnlyList<SourceDocument>> ListAsync(CancellationToken cancellationToken);
}

public sealed class DocumentChunker(int targetCharacters = 1200, int overlapCharacters = 180)
{
    public IReadOnlyList<DocumentChunk> Chunk(string sourceId, string text)
    {
        var normalized = string.Join('\n', text.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0));
        var chunks = new List<DocumentChunk>();
        var start = 0;
        var index = 0;
        while (start < normalized.Length)
        {
            var end = Math.Min(start + targetCharacters, normalized.Length);
            if (end < normalized.Length)
            {
                var boundary = normalized.LastIndexOfAny([' ', '\n', '.', ',', ';'], end - 1, Math.Min(120, end - start));
                if (boundary > start + targetCharacters / 2) end = boundary + 1;
            }
            var value = normalized[start..end].Trim();
            if (value.Length > 0)
                chunks.Add(new DocumentChunk($"{sourceId}:{index}", index++, value, null, string.Empty));
            if (end >= normalized.Length) break;
            start = Math.Max(end - overlapCharacters, start + 1);
        }
        return chunks;
    }

    public static string Hash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

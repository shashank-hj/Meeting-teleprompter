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
    IReadOnlyList<DocumentChunk> Chunks,
    string Status = "indexed",
    string? LastError = null,
    DateTimeOffset? LastAttemptAt = null,
    int ParserVersion = 2)
{
    public string StatusDescription => Status == "failed"
        ? (ContentHash.Length > 0 ? "Refresh failed · saved copy retained" : "Import failed · refresh to retry")
        : (Type == "url" ? "Saved " : "Indexed ") + UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}

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
        if (targetCharacters < 32 || overlapCharacters < 0 || overlapCharacters >= targetCharacters)
            throw new ArgumentOutOfRangeException(nameof(targetCharacters), "Chunk overlap must be smaller than its target size.");
        var chunks = new List<DocumentChunk>();
        var heading = string.Empty;
        var section = new StringBuilder();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^#{1,6}\s+"))
            {
                Flush();
                heading = trimmed.TrimStart('#', ' ').TrimEnd('#', ' ');
            }
            else section.AppendLine(trimmed);
        }
        Flush();
        return chunks;

        void Flush()
        {
            var normalized = section.ToString().Trim();
            section.Clear();
            var start = 0;
            while (start < normalized.Length)
            {
                var end = Math.Min(start + targetCharacters, normalized.Length);
                if (end < normalized.Length)
                {
                    // Prefer paragraph/list boundaries, then words, without crossing a heading.
                    var boundary = normalized.LastIndexOf('\n', end - 1, end - start);
                    if (boundary < start + targetCharacters / 2)
                        boundary = normalized.LastIndexOfAny([' ', '.', ',', ';'], end - 1, Math.Min(120, end - start));
                    if (boundary > start + targetCharacters / 2) end = boundary + 1;
                }
                var value = normalized[start..end].Trim();
                if (value.Length > 0) chunks.Add(new($"{sourceId}:{chunks.Count}", chunks.Count, value, null, heading));
                if (end >= normalized.Length) break;
                start = Math.Max(end - overlapCharacters, start + 1);
            }
        }
    }

    public static string Hash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

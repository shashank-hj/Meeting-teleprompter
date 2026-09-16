using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using AngleSharp.Html.Parser;
using UglyToad.PdfPig;

namespace MeetingTeleprompter.Core;

public sealed class DocumentIngestor(SqliteSourceStore store)
{
    private readonly SafeUrlFetcher _fetcher = new(new());
    private readonly DocumentChunker _chunker = new();
    public static bool Supports(string path) => Path.GetExtension(path).ToLowerInvariant() is ".txt" or ".md" or ".html" or ".htm" or ".pdf" or ".docx";
    public static string FileId(string path) => DocumentChunker.Hash(Path.GetFullPath(path).ToUpperInvariant());

    public async Task IndexFileAsync(string path, CancellationToken ct)
    {
        path = Path.GetFullPath(path);
        if (!Supports(path)) return;
        if (!File.Exists(path)) { await store.RemoveAsync(FileId(path), ct); return; }
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return;
        if (info.Length > 20 * 1024 * 1024) throw new InvalidOperationException("Files must be smaller than 20 MB.");
        var bytes = await File.ReadAllBytesAsync(path, ct);
        await IndexAsync(FileId(path), "file", path, Path.GetFileName(path), bytes, Path.GetExtension(path), ct);
    }

    public async Task IndexUrlAsync(string url, CancellationToken ct)
    {
        var uri = new Uri(url, UriKind.Absolute);
        var result = await _fetcher.FetchAsync(uri, ct);
        var extension = result.ContentType switch
        {
            "text/html" or "application/xhtml+xml" => ".html",
            "text/plain" or "text/markdown" => ".txt",
            "application/pdf" => ".pdf",
            _ => throw new InvalidOperationException("URL must serve HTML, plain text, or PDF.")
        };
        // Retain the supplied URL as the refresh key even after redirects.
        await IndexAsync(DocumentChunker.Hash(uri.AbsoluteUri), "url", uri.AbsoluteUri, uri.Host, result.Content, extension, ct);
    }

    private async Task IndexAsync(string id, string type, string location, string title, byte[] bytes, string extension, CancellationToken ct)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        var previous = (await store.ListAsync(ct)).FirstOrDefault(x => x.Id == id);
        if (type == "file" && previous?.ContentHash == hash) return;
        var chunks = new List<DocumentChunk>();
        void Add(string text, int? page = null)
        {
            foreach (var chunk in _chunker.Chunk(id, text))
                chunks.Add(chunk with { Id = id + ":" + chunks.Count, Index = chunks.Count, Page = page });
        }
        ct.ThrowIfCancellationRequested();
        switch (extension.ToLowerInvariant())
        {
            case ".pdf":
                using (var pdf = PdfDocument.Open(bytes))
                    foreach (var page in pdf.GetPages()) { ct.ThrowIfCancellationRequested(); Add(page.Text, page.Number); }
                break;
            case ".docx":
                using (var zip = new ZipArchive(new MemoryStream(bytes)))
                {
                    var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidOperationException("DOCX has no document body.");
                    if (entry.Length > 20 * 1024 * 1024) throw new InvalidOperationException("Expanded document exceeds size limit.");
                    using var stream = entry.Open();
                    using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 20 * 1024 * 1024, XmlResolver = null });
                    var document = XDocument.Load(reader);
                    XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
                    Add(string.Join("\n", document.Descendants(w + "p").Select(p => string.Concat(p.Descendants(w + "t").Select(t => t.Value)))));
                }
                break;
            case ".html": case ".htm":
                var html = await new HtmlParser().ParseDocumentAsync(Encoding.UTF8.GetString(bytes), ct);
                foreach (var node in html.QuerySelectorAll("script,style,nav,footer,noscript")) node.Remove();
                title = string.IsNullOrWhiteSpace(html.Title) ? title : html.Title;
                Add(string.Join("\n", html.QuerySelectorAll("h1,h2,h3,p,li,pre,td").Select(x => x.TextContent)));
                if (chunks.Count == 0) Add(html.Body?.TextContent ?? "");
                break;
            default: Add(Encoding.UTF8.GetString(bytes)); break;
        }
        if (chunks.Count == 0) throw new InvalidOperationException("No extractable text. Scanned PDFs require OCR before import.");
        await store.UpsertAsync(new(id, type, location, title, DateTimeOffset.UtcNow, hash, chunks), ct);
    }

    public async Task<int> IndexFolderAsync(string folder, CancellationToken ct, Action<string>? error = null)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
        var count = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System };
        foreach (var file in Directory.EnumerateFiles(folder, "*", options).Where(Supports))
        {
            ct.ThrowIfCancellationRequested();
            try { await IndexFileAsync(file, ct); count++; }
            catch (Exception ex) when (ex is not OperationCanceledException) { if (error is null) throw; error(Path.GetFileName(file) + ": " + ex.Message); }
        }
        return count;
    }
}

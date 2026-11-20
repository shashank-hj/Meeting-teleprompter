using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using AngleSharp.Html.Parser;
using UglyToad.PdfPig;

namespace MeetingTeleprompter.Core;

public sealed class DocumentIngestor(SqliteSourceStore store, IWebSourceFetcher? fetcher = null, IWebPageRenderer? renderer = null, bool renderJavaScript = true)
{
    private readonly IWebSourceFetcher _fetcher = fetcher ?? new SafeUrlFetcher(new());
    private readonly IWebPageRenderer _renderer = renderer ?? new EdgePageRenderer();
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
        SafeUrlFetcher.ValidateUri(uri);
        try
        {
        var result = await _fetcher.FetchAsync(uri, ct);
        var extension = result.ContentType switch
        {
            "text/html" or "application/xhtml+xml" => ".html",
            "text/plain" or "text/markdown" => ".txt",
            "application/pdf" => ".pdf",
            _ => throw new InvalidOperationException("URL must serve HTML, plain text, or PDF.")
        };
        var content = result.Content;
        if (extension == ".html" && renderJavaScript)
        {
            var parsed = await new HtmlParser().ParseDocumentAsync(Encoding.UTF8.GetString(content), ct);
            var hasScripts = parsed.QuerySelector("script") is not null;
            foreach (var node in parsed.QuerySelectorAll("script,style,nav,footer,noscript")) node.Remove();
            if (hasScripts && (parsed.Body?.TextContent.Trim().Length ?? 0) < 120)
            {
                try { content = Encoding.UTF8.GetBytes(await _renderer.RenderAsync(result.FinalUri, content, ct)); }
                catch (Microsoft.Playwright.PlaywrightException ex)
                { throw new InvalidOperationException("This page needs JavaScript. Install Microsoft Edge or supply an exported HTML/PDF copy. " + ex.Message); }
            }
        }
        // Retain the supplied URL as the refresh key even after redirects.
        await IndexAsync(DocumentChunker.Hash(uri.AbsoluteUri), "url", uri.AbsoluteUri, uri.Host, content, extension, ct);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            await store.RecordUrlFailureAsync(uri, ex.Message, ct);
            throw;
        }
    }

    private async Task IndexAsync(string id, string type, string location, string title, byte[] bytes, string extension, CancellationToken ct)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        var previous = (await store.ListAsync(ct)).FirstOrDefault(x => x.Id == id);
        if (type == "file" && previous?.ContentHash == hash && previous.ParserVersion == 2) return;
        var chunks = new List<DocumentChunk>();
        var occurrences = new Dictionary<string, int>();
        void Add(string text, int? page = null)
        {
            foreach (var chunk in _chunker.Chunk(id, text))
            {
                var key = id + ":" + DocumentChunker.Hash(page + "\n" + chunk.Heading + "\n" + chunk.Text)[..24];
                var occurrence = occurrences.GetValueOrDefault(key);
                occurrences[key] = occurrence + 1;
                chunks.Add(chunk with { Id = key + ":" + occurrence, Index = chunks.Count, Page = page });
            }
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
                    Add(string.Join("\n\n", document.Descendants(w + "p").Select(p =>
                    {
                        var value = string.Concat(p.Descendants(w + "t").Select(t => t.Value));
                        var style = p.Element(w + "pPr")?.Element(w + "pStyle")?.Attribute(w + "val")?.Value;
                        return style?.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) == true ? "## " + value : value;
                    })));
                }
                break;
            case ".html": case ".htm":
                var html = await new HtmlParser().ParseDocumentAsync(Encoding.UTF8.GetString(bytes), ct);
                foreach (var node in html.QuerySelectorAll("script,style,nav,footer,noscript,header,aside")) node.Remove();
                title = string.IsNullOrWhiteSpace(html.Title) ? title : html.Title;
                var body = html.QuerySelector("article,main,[role=main]") ?? html.Body;
                Add(string.Join("\n\n", body?.QuerySelectorAll("h1,h2,h3,h4,h5,h6,p,li,pre,td").Select(x =>
                    x.LocalName.StartsWith('h') ? "## " + x.TextContent : x.TextContent) ?? []));
                if (chunks.Count == 0) Add(body?.TextContent ?? "");
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

    public async Task ReconcileFolderAsync(string folder, CancellationToken ct, Action<string>? error = null)
    {
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        if (Directory.Exists(folder)) await IndexFolderAsync(folder, ct, error);
        foreach (var source in await store.ListAsync(ct))
            if (source.Type == "file" && source.Location.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !File.Exists(source.Location))
                await store.RemoveAsync(source.Id, ct);
    }
}

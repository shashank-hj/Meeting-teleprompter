using System.Net;
using System.Text;
using MeetingTeleprompter.Core;

internal static class ProposalTests
{
    public static async Task<int> RunAsync(bool withBrowser)
    {
        var checks = 0;
        void Check(bool value, string name)
        { if (!value) throw new Exception("FAIL: " + name); checks++; Console.WriteLine("PASS: " + name); }
        var root = Path.Combine(Path.GetTempPath(), "teleprompter-proposal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = new SqliteSourceStore(Path.Combine(root, "sources.db"));
            var file = Path.Combine(root, "release.md");
            var ingestor = new DocumentIngestor(store);
            await File.WriteAllTextAsync(file, "# Release\n\nOrion release is Friday.\n\n## Budget\n\nApproved budget is 25000 dollars.");
            await ingestor.IndexFileAsync(file, default);
            var original = await store.AllChunksAsync(default);
            Check(original.Count == 2 && original.Select(x => x.Source.Heading).SequenceEqual(new[] { "Release", "Budget" }), "Markdown sections preserve citation headings");
            foreach (var chunk in original) await store.SaveVectorAsync(chunk.Id, "fixture", [1, 0], default);
            await File.WriteAllTextAsync(file, "# Inserted\nNew owners are Maya and Alan.\n\n# Release\nOrion release is Friday.\n\n## Budget\nApproved budget is 30000 dollars.");
            await ingestor.IndexFileAsync(file, default);
            var updated = await store.AllChunksAsync(default);
            var vectors = await store.VectorsAsync("fixture", default);
            Check(vectors.Count == 1 && vectors.ContainsKey(original[0].Id) && updated.Any(x => x.Id == original[0].Id && x.Source.ChunkIndex == 1), "inserting a section preserves unchanged embeddings and updates chunk locations");
            Check((await store.SearchAsync("25000", 10, default)).Count == 0 && (await store.SearchAsync("30000", 10, default)).Count == 1,
                "incremental refresh replaces changed full-text entries");
            await store.RemoveAsync(updated[0].Source.Id, default);
            Check((await store.AllChunksAsync(default)).Count == 0 && (await store.VectorsAsync("fixture", default)).Count == 0, "incremental source deletion removes every derived row");
            var manual = new SourceDocument("manual", "file", Path.Combine(root, "manual.txt"), "Notes", DateTimeOffset.UtcNow, "hash", [new("fixed", 0, "Alpha budget", null, "Orion")]);
            await store.UpsertAsync(manual, default); await store.SaveVectorAsync("fixed", "fixture", [1, 0], default);
            Check((await store.SearchAsync("Orion", 5, default)).Count == 1, "source headings are searchable even when absent from passage text");
            await store.UpsertAsync(manual with { Chunks = [new("fixed", 0, "Beta budget", null, "Apollo")] }, default);
            Check((await store.SearchAsync("Alpha", 5, default)).Count == 0 && (await store.SearchAsync("Apollo", 5, default)).Count == 1 && (await store.VectorsAsync("fixture", default)).Count == 0,
                "in-place chunk revisions update FTS and invalidate stale vectors transactionally");
            await store.RemoveAsync("manual", default);
            var legacyFile = Path.Combine(root, "legacy.md");
            await File.WriteAllTextAsync(legacyFile, "# LegacyOrion\nThe approved budget is retained.");
            await ingestor.IndexFileAsync(legacyFile, default);
            var legacySource = (await store.ListAsync(default)).Single(x => x.Location == legacyFile);
            await store.UpsertAsync(legacySource with { ParserVersion = 0, Chunks = [new("legacy", 0, "Old parser text", null, "")] }, default);
            await ingestor.IndexFileAsync(legacyFile, default);
            Check((await store.SearchAsync("LegacyOrion", 5, default)).Count == 1 && (await store.ListAsync(default)).Single(x => x.Location == legacyFile).ParserVersion == 2,
                "parser upgrades rebuild unchanged legacy files while normal unchanged imports remain incremental");
            await store.RemoveAsync(legacySource.Id, default);

            var fetch = new FixtureFetcher("<title>Release notes</title><main><h2>Launch</h2><p>Orion launches Friday with Maya.</p></main>");
            var renderer = new FixtureRenderer();
            var web = new DocumentIngestor(store, fetch, renderer);
            await web.IndexUrlAsync("https://example.com/release", default);
            var first = (await store.SearchAsync("Orion launches", 5, default)).Single();
            Check(fetch.Calls == 1 && renderer.Calls == 0 && first.Source.FetchedAt is not null && first.Source.CanonicalLocation == "https://example.com/release", "URL is indexed immediately with supplied URL and freshness");
            await store.SaveVectorAsync(first.Id, "fixture", [1, 0], default);
            fetch.Html = "<main><p>Orion launches Monday with Alan.</p></main>";
            await store.SearchAsync("Orion", 5, default); await store.ListAsync(default);
            Check(fetch.Calls == 1 && (await store.SearchAsync("Friday", 5, default)).Count == 1, "retrieval never refreshes a saved URL");
            await web.IndexUrlAsync("https://example.com/release", default);
            Check(fetch.Calls == 2 && (await store.SearchAsync("Monday", 5, default)).Count == 1 && (await store.SearchAsync("Friday", 5, default)).Count == 0, "explicit URL refresh replaces its saved snapshot");
            var saved = (await store.SearchAsync("Monday", 5, default)).Single();
            await store.SaveVectorAsync(saved.Id, "fixture", [1, 0], default);
            await web.IndexUrlAsync("https://example.com/release", default);
            Check((await store.VectorsAsync("fixture", default)).ContainsKey(saved.Id), "refreshing unchanged URL content preserves its embeddings");
            fetch.Fail = true;
            try { await web.IndexUrlAsync("https://example.com/release", default); } catch (HttpRequestException) { }
            var failed = (await store.ListAsync(default)).Single(x => x.Location == "https://example.com/release");
            Check(failed.Status == "failed" && failed.LastError is not null && (await store.SearchAsync("Monday", 5, default)).Count == 1,
                "failed URL refresh records its status and keeps the searchable saved copy");
            fetch.Fail = false;
            fetch.Html = "<main id=app></main><script src='/app.js'></script>";
            await web.IndexUrlAsync("https://example.com/javascript", default);
            Check(renderer.Calls == 1 && (await store.SearchAsync("rendered knowledge", 5, default)).Count == 1, "JavaScript-only sources invoke the renderer fallback");
            Check(!EdgePageRenderer.AllowsRequest(new("https://example.com/"), "http://127.0.0.1/secrets", "GET", "fetch") &&
                !EdgePageRenderer.AllowsRequest(new("https://example.com/"), "https://other.example/", "GET", "script") &&
                !EdgePageRenderer.AllowsRequest(new("https://example.com/"), "https://example.com/send", "POST", "fetch") &&
                EdgePageRenderer.AllowsRequest(new("https://example.com/"), "https://example.com/app.js", "GET", "script"), "browser fallback blocks private addresses, third-party origins, and writes");

            async Task Reject(Func<HttpResponseMessage> response, UrlSafetyPolicy policy, string name)
            {
                var rejected = false;
                try { await new SafeUrlFetcher(policy, new FixtureTransport(response)).FetchAsync(new("https://example.com/"), default); }
                catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or OperationCanceledException) { rejected = true; }
                Check(rejected, name);
            }
            await Reject(() => new(HttpStatusCode.Redirect) { Headers = { Location = new("http://169.254.169.254/metadata") } }, new(), "URL redirects cannot reach metadata or private networks");
            await Reject(() => new(HttpStatusCode.Redirect) { Headers = { Location = new("https://example.com/again") } }, new() { MaxRedirects = 1 }, "URL redirect chains are bounded");
            await Reject(() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[100]) }, new() { MaxBytes = 20 }, "URL content-length limit is enforced");
            await Reject(() => new(HttpStatusCode.OK) { Content = new StringContent("compressed") { Headers = { ContentEncoding = { "gzip" } } } }, new(), "compressed HTTP responses are rejected");
            var diversity = HybridRetriever.Fuse(updated, [], 5);
            Check(diversity.Count <= 2, "retrieval limits repetitive passages from a single source");

            var stable = new TranscriptStabilizer();
            var originalEvent = new TranscriptEvent("revised", "remote", TranscriptKind.Final, "Orion launches Friday.", TimeSpan.Zero, TimeSpan.FromSeconds(2), .8, DateTimeOffset.UtcNow);
            var state = new ConversationState(); state.Add(stable.Accept(originalEvent).Single());
            var correction = stable.Accept(originalEvent with { Text = "Orion launches Monday." }).Single(); state.Add(correction);
            Check(correction.IsCorrection && state.Snapshot().RecentUtterances.Count == 1 && state.Snapshot().RecentUtterances[0].Text.Contains("Monday"), "final transcript corrections replace the canonical utterance without duplication");
            Check(stable.Accept(originalEvent with { Text = "Orion launches Monday." }).Count == 0, "identical final corrections are suppressed");

            var watched = Path.Combine(root, "watched"); Directory.CreateDirectory(watched);
            var changed = new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true };
            var changes = System.Threading.Channels.Channel.CreateUnbounded<string>(changed);
            var retries = 0;
            using (var watcher = new FileSourceWatcher(watched, (path, ct) =>
            {
                if (Interlocked.Increment(ref retries) <= 2) throw new IOException("Simulated interrupted write.");
                changes.Writer.TryWrite(path); return Task.CompletedTask;
            }, TimeSpan.FromMilliseconds(40), _ => { changes.Writer.TryWrite("reconciled"); return Task.CompletedTask; }))
            {
                var note = Path.Combine(watched, "note.md");
                await File.WriteAllTextAsync(note, "Complete document after an interrupted write.");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                while (await changes.Reader.ReadAsync(timeout.Token) != note) { }
                Check(retries >= 3, "folder watcher retries interrupted writes after debouncing");
                var renamed = Path.Combine(watched, "renamed.md"); File.Move(note, renamed);
                var observed = new HashSet<string>();
                while (!observed.Contains(note) || !observed.Contains(renamed)) observed.Add(await changes.Reader.ReadAsync(timeout.Token));
                Check(true, "folder watcher reports both sides of a file rename");
                File.Delete(renamed);
                while (await changes.Reader.ReadAsync(timeout.Token) != renamed) { }
                Check(true, "folder watcher reports file deletion");
                Directory.CreateDirectory(Path.Combine(watched, "nested"));
                while (await changes.Reader.ReadAsync(timeout.Token) != "reconciled") { }
                Check(true, "folder watcher reconciles directory changes");
            }
            Check(RuntimeResourceMonitor.AvailableMemoryBytes() is > 0 || !OperatingSystem.IsWindows(), "device check measures available Windows memory");
            await using (var runtimes = new NativeRuntimeManager())
            {
                var constrained = new BudgetedGenerator(new NeverGenerator(), runtimes, new(), () => 256L * 1024 * 1024);
                var evidence = new[] { new EvidenceChunk("budget", "Orion's approved budget is 25000 dollars.", new("budget", "file", "notes.md", "Budget", null, "", null, 0), 1) };
                var conversation = new ConversationState(); conversation.Add(new("budget", "typed", "What is the Orion budget?", TimeSpan.Zero, TimeSpan.Zero, 1, DateTimeOffset.UtcNow));
                var fallback = await new AnswerService(new FixtureRetriever(evidence), constrained).AnswerAsync(conversation.Snapshot(), "concise", true, WorkPriority.UserGeneration, default);
                Check(!fallback.IsGeneratedAssistance && fallback.Sources.Count == 1 && fallback.Text.Contains("25000"), "memory pressure keeps cited source evidence available without loading generation");
            }
            if (withBrowser)
            {
                const string html = """
                    <html><body><main><p id="result">Waiting</p></main><script>
                    Promise.all(['http://127.0.0.1:8178/health','https://other.example/secret'].map(url => fetch(url).then(() => false, () => true)))
                      .then(blocked => document.querySelector('#result').textContent = blocked.every(Boolean) ? 'Rendered knowledge: private and third-party requests blocked.' : 'NETWORK LEAK');
                    </script></body></html>
                    """;
                var rendered = await new EdgePageRenderer().RenderAsync(new("https://example.com/"), Encoding.UTF8.GetBytes(html), default);
                Check(rendered.Contains("Rendered knowledge: private and third-party requests blocked."), "installed Edge renders JavaScript while private and third-party fetches fail");
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
        return checks;
    }
    private sealed class FixtureFetcher(string html) : IWebSourceFetcher
    {
        public string Html = html;
        public int Calls;
        public bool Fail;
        public Task<(Uri FinalUri, string ContentType, byte[] Content)> FetchAsync(Uri uri, CancellationToken ct)
        { Calls++; if (Fail) throw new HttpRequestException("Source unavailable."); return Task.FromResult((uri, "text/html", Encoding.UTF8.GetBytes(Html))); }
    }
    private sealed class FixtureRenderer : IWebPageRenderer
    {
        public int Calls;
        public Task<string> RenderAsync(Uri uri, byte[] html, CancellationToken ct)
        { Calls++; return Task.FromResult("<main><p>Rendered knowledge is now searchable with citations.</p></main>"); }
    }
    private sealed class FixtureTransport(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(response());
    }
    private sealed class FixtureRetriever(IReadOnlyList<EvidenceChunk> evidence) : IRetriever
    {
        public Task<IReadOnlyList<EvidenceChunk>> SearchAsync(string query, int limit, CancellationToken ct) => Task.FromResult(evidence);
    }
    private sealed class NeverGenerator : IGenerator
    {
        public IAsyncEnumerable<string> StreamAsync(SuggestionRequest request, CancellationToken ct) => throw new Exception("Memory-constrained generation must not run.");
    }
}

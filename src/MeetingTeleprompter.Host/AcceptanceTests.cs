using System.Net;
using MeetingTeleprompter.Core;

internal static class AcceptanceTests
{
    public static async Task RunAsync()
    {
        var passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + name);
            passed++; Console.WriteLine("PASS: " + name);
        }
        var stabilizer = new TranscriptStabilizer();
        TranscriptEvent Entry(string id, string text, int start = 0, string channel = "remote", TranscriptKind kind = TranscriptKind.Final) =>
            new(id, channel, kind, text, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(start + 4), .9, DateTimeOffset.UtcNow);
        Check(stabilizer.Accept(Entry("a", "partial", kind: TranscriptKind.Partial)).Count == 0, "partials do not trigger");
        Check(stabilizer.Accept(Entry("a", "we agreed the launch is Friday")).Count == 1, "stable final emitted");
        Check(stabilizer.Accept(Entry("a", "we agreed the launch is Friday")).Count == 0, "duplicate suppressed");
        Check(stabilizer.Accept(Entry("b", "launch is Friday and budget is approved", 2))[0].Text == "and budget is approved", "overlap preserves new suffix");
        Check(stabilizer.Accept(Entry("a", "local channel", channel: "local")).Count == 1, "channel identities independent");
        Check(stabilizer.Accept(Entry("c", "go go go go now", 10))[0].Text.EndsWith("now"), "legitimate repetition preserved");
        foreach (var ip in new[] { "127.0.0.1", "10.1.2.3", "169.254.169.254", "100.64.0.1", "::1", "fc00::1", "::ffff:192.168.0.1" })
            Check(!SafeUrlFetcher.IsPublic(IPAddress.Parse(ip)), "block " + ip);
        Check(SafeUrlFetcher.IsPublic(IPAddress.Parse("8.8.8.8")), "public IPv4");
        var chunker = new DocumentChunker();
        var chunks = chunker.Chunk("test", new string('a', 3000));
        Check(chunks.Count >= 3 && chunks.All(x => x.Text.Length <= 1200), "bounded chunks");
        var root = Path.Combine(Path.GetTempPath(), "teleprompter-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = new SqliteSourceStore(Path.Combine(root, "test.db"));
            var ingestion = new DocumentIngestor(store);
            var file = Path.Combine(root, "notes.md");
            await File.WriteAllTextAsync(file, "Orion launch budget is 25000 dollars.");
            await ingestion.IndexFileAsync(file, default);
            Check((await store.SearchAsync("Orion", 5, default)).Count == 1, "file ingestion and FTS");
            await File.WriteAllTextAsync(file, "Apollo launch budget is approved.");
            await ingestion.IndexFileAsync(file, default);
            Check((await store.SearchAsync("Orion", 5, default)).Count == 0, "refresh removes old FTS rows");
            Check((await store.SearchAsync("Apollo", 5, default)).Count == 1, "refresh searchable");
            var evidence = (await store.SearchAsync("Apollo", 5, default))[0];
            await store.SaveVectorAsync(evidence.Id, "test-v1", [1f, 0f], default);
            Check((await store.VectorsAsync("test-v2", default)).Count == 0, "embedding versions isolated");
            await store.RemoveAsync(evidence.Source.Id, default);
            Check((await store.SearchAsync("Apollo", 5, default)).Count == 0 && (await store.VectorsAsync("test-v1", default)).Count == 0, "source removal cascades to index and vectors");
            Check(HybridRetriever.Fuse([evidence], [evidence], 5).Count == 1, "hybrid fusion deduplicates chunks");
            var engine = new TriggerEngine(TimeSpan.FromMinutes(1));
            var utterance = new StableUtterance("trigger", "remote", "Orion budget needs approval", TimeSpan.Zero, TimeSpan.FromSeconds(1), .9, DateTimeOffset.UtcNow);
            var state = new ConversationState(); state.Add(utterance);
            Check(engine.Evaluate(utterance, state.Snapshot()).ShouldGenerate, "proactive trigger without question");
            Check(!engine.Evaluate(utterance, state.Snapshot()).ShouldGenerate, "trigger cooldown");
            var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (var scheduler = new WorkScheduler())
            {
                scheduler.Failed += _ => failed.TrySetResult();
                scheduler.Enqueue(WorkPriority.UserGeneration, _ => throw new InvalidOperationException("expected"));
                await failed.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var survived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                scheduler.Enqueue(WorkPriority.UserGeneration, _ => { survived.SetResult(); return Task.CompletedTask; });
                await survived.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Check(true, "scheduler reports errors and survives");
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
        await PipelineTests.RunAsync();
        passed++;
        await WorkerTests.RunAsync();
        passed++;
        Console.WriteLine(passed + " acceptance checks passed.");
    }
}

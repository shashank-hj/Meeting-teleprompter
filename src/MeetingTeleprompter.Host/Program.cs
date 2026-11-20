using System.IO.Pipes;
using System.Text.Json;
using MeetingTeleprompter.Core;

if (args.Length >= 2 && args[0] is "asr-worker" or "generator-worker")
{
    await using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous);
    await pipe.ConnectAsync(15000);
    using var reader = new StreamReader(pipe, leaveOpen: true);
    await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
    while (await reader.ReadLineAsync() is { } line)
    {
        WorkerRequest? request = null;
        try
        {
            request = JsonSerializer.Deserialize<WorkerRequest>(line) ?? throw new InvalidOperationException("Empty request.");
            if (request.Protocol != Protocol.Version) throw new InvalidOperationException("Unsupported protocol.");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            if (request.Deadline is { } expires)
            {
                var remaining = expires - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException("Worker request deadline expired.");
                deadline.CancelAfter(remaining < TimeSpan.FromSeconds(90) ? remaining : TimeSpan.FromSeconds(90));
            }
            GenerationTelemetry? telemetry = null;
            if (args[0] == "asr-worker" && request.Operation == "transcribe")
            {
                var result = await WhisperInference.TranscribeDetailedAsync(request.Endpoint, request.Audio ?? throw new InvalidOperationException("Missing audio."), deadline.Token, request.SpeechVocabulary, request.SpeechBeamSize);
                await writer.WriteLineAsync(JsonSerializer.Serialize(new WorkerResponse(Protocol.Version, request.Id, result.Text, Confidence: result.Confidence)));
            }
            else if (args[0] == "generator-worker" && request.Operation == "generate")
            {
                var generation = new SuggestionRequest(request.Id, request.Priority, request.Route ?? "concise",
                    request.Conversation!, request.Evidence!, deadline.Token, request.Basis);
                var generator = new LlamaGenerator(request.Endpoint);
                await foreach (var token in generator.StreamAsync(generation, deadline.Token))
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new WorkerResponse(Protocol.Version, request.Id, token)));
                telemetry = generator.LastTelemetry;
            }
            else throw new InvalidOperationException("Unsupported worker operation.");
            await writer.WriteLineAsync(JsonSerializer.Serialize(new WorkerResponse(Protocol.Version, request.Id, Done: true, Telemetry: telemetry)));
        }
        catch (Exception ex)
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new WorkerResponse(Protocol.Version, request?.Id ?? "", Done: true, Error: ex.Message)));
        }
    }
}
else if (args.Length == 4 && args[0] == "--benchmark-wav")
{
    var settings = JsonSerializer.Deserialize<RuntimeSettings>(await File.ReadAllTextAsync(args[1]))!;
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    var result = await DeviceBenchmark.RunAsync(settings, WaveAudio.ReadPcm(args[2]), timeout.Token);
    await File.WriteAllTextAsync(args[3], JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(DeviceBenchmark.Describe(result));
}
else if (args.Length == 4 && args[0] == "--validate-models") await ModelValidation.RunAsync(args[1], args[2], args[3]);
else if (args.Length == 3 && args[0] == "--benchmark") await Benchmark.RunAsync(args[1], args[2]);
else if (args.Contains("--self-test")) await AcceptanceTests.RunAsync(args.Contains("--with-browser"));
else Console.WriteLine("Meeting Teleprompter worker host. Run the desktop app or use --self-test.");

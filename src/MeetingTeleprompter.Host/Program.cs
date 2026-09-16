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
            if (args[0] == "asr-worker" && request.Operation == "transcribe")
            {
                var text = await WhisperInference.TranscribeAsync(request.Endpoint, request.Audio ?? throw new InvalidOperationException("Missing audio."), deadline.Token);
                await writer.WriteLineAsync(JsonSerializer.Serialize(new WorkerResponse(Protocol.Version, request.Id, text)));
            }
            else if (args[0] == "generator-worker" && request.Operation == "generate")
            {
                var generation = new SuggestionRequest(request.Id, WorkPriority.UserGeneration, request.Route ?? "concise",
                    request.Conversation!, request.Evidence!, deadline.Token);
                await foreach (var token in new LlamaGenerator(request.Endpoint).StreamAsync(generation, deadline.Token))
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new WorkerResponse(Protocol.Version, request.Id, token)));
            }
            else throw new InvalidOperationException("Unsupported worker operation.");
            await writer.WriteLineAsync(JsonSerializer.Serialize(new WorkerResponse(Protocol.Version, request.Id, Done: true)));
        }
        catch (Exception ex)
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new WorkerResponse(Protocol.Version, request?.Id ?? "", Done: true, Error: ex.Message)));
        }
    }
}
else if (args.Length == 4 && args[0] == "--validate-models") await ModelValidation.RunAsync(args[1], args[2], args[3]);
else if (args.Length == 3 && args[0] == "--benchmark") await Benchmark.RunAsync(args[1], args[2]);
else if (args.Contains("--self-test")) await AcceptanceTests.RunAsync();
else Console.WriteLine("Meeting Teleprompter worker host. Run the desktop app or use --self-test.");

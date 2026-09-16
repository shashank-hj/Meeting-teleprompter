using System.Net;
using System.Net.Sockets;
using System.Text;
using MeetingTeleprompter.Core;

internal static class WorkerTests
{
    public static async Task RunAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            for (var request = 0; request < 2; request++)
            {
                using var client = await listener.AcceptTcpClientAsync(deadline.Token);
                await using var stream = client.GetStream();
                var header = new StringBuilder();
                var one = new byte[1];
                while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    if (await stream.ReadAsync(one, deadline.Token) == 0) throw new IOException("Incomplete HTTP request.");
                    header.Append((char)one[0]);
                    if (header.Length > 16000) throw new IOException("Header too large.");
                }
                var lengthLine = header.ToString().Split("\r\n").First(x => x.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                var body = new byte[int.Parse(lengthLine.Split(':')[1].Trim())];
                await stream.ReadExactlyAsync(body, deadline.Token);
                if (!Encoding.UTF8.GetString(body).Contains("audio.wav")) throw new Exception("Audio was not posted as a WAV multipart field.");
                var responseBody = request == 0 ? "{}" : "{\"text\":\"Local worker recovery succeeded.\"}";
                var bytes = Encoding.UTF8.GetBytes(responseBody);
                var responseHeader = Encoding.ASCII.GetBytes($"HTTP/1.1 {(request == 0 ? "500 Error" : "200 OK")}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(responseHeader, deadline.Token);
                await stream.WriteAsync(bytes, deadline.Token);
            }
        }, deadline.Token);
        await using var worker = new PipeInferenceClient(Environment.ProcessPath!, "asr-worker");
        var failed = false;
        try
        {
            await foreach (var unused in worker.StreamAsync(new(Protocol.Version, "failure", $"http://127.0.0.1:{port}/", "transcribe", new byte[3200]), deadline.Token)) { }
        }
        catch (InvalidOperationException) { failed = true; }
        if (!failed) throw new Exception("Worker failure was not reported.");
        var text = new StringBuilder();
        await foreach (var part in worker.StreamAsync(new(Protocol.Version, "recovery", $"http://127.0.0.1:{port}/", "transcribe", new byte[3200]), deadline.Token)) text.Append(part);
        if (text.ToString() != "Local worker recovery succeeded.") throw new Exception("Worker did not recover.");
        await server;
        Console.WriteLine("PASS: named-pipe worker transports WAV audio, reports inference failures, and restarts");
    }
}

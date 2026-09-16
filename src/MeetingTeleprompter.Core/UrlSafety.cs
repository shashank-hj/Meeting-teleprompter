using System.Net;
using System.Net.Sockets;

namespace MeetingTeleprompter.Core;

public sealed class UrlSafetyPolicy
{
    public int MaxRedirects { get; init; } = 5;
    public long MaxBytes { get; init; } = 10 * 1024 * 1024;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);
}

public sealed class SafeUrlFetcher(UrlSafetyPolicy policy)
{
    public static void ValidateUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
            throw new InvalidOperationException("Only public HTTP/HTTPS URLs without credentials are supported.");
        if (uri.IsLoopback || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Local network URLs are blocked.");
        if (IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out var address) && !IsPublic(address))
            throw new InvalidOperationException("Local network URLs are blocked.");
    }

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return IsPublic(address.MapToIPv4());
        if (IPAddress.IsLoopback(address)) return false;
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return (b[0] & 0xe0) == 0x20 && !(b[0] == 0x20 && b[1] == 1 && b[2] == 0x0d && b[3] == 0xb8);
        return b[0] is not (0 or 10 or 127) && b[0] < 224
            && !(b[0] == 100 && b[1] is >= 64 and <= 127)
            && !(b[0] == 169 && b[1] == 254)
            && !(b[0] == 172 && b[1] is >= 16 and <= 31)
            && !(b[0] == 192 && b[1] is 0 or 168)
            && !(b[0] == 198 && b[1] is 18 or 19);
    }

    public async Task<(Uri FinalUri, string ContentType, byte[] Content)> FetchAsync(Uri uri, CancellationToken cancellationToken)
    {
        ValidateUri(uri);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(policy.Timeout);
        var ct = deadline.Token;
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseProxy = false, AutomaticDecompression = DecompressionMethods.None,
            ConnectCallback = async (context, token) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
                if (addresses.Length == 0 || addresses.Any(x => !IsPublic(x)))
                    throw new InvalidOperationException("Hostname resolves to a non-public address.");
                // Connect to the validated address, preventing DNS rebinding.
                var socket = new Socket(addresses[0].AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(new IPEndPoint(addresses[0], context.DnsEndPoint.Port), token); return new NetworkStream(socket, ownsSocket: true); }
                catch { socket.Dispose(); throw; }
            }
        };
        using var client = new HttpClient(handler);
        var current = uri;
        for (var redirects = 0; redirects <= policy.MaxRedirects; redirects++)
        {
            ValidateUri(current);
            using var response = await client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                current = new Uri(current, response.Headers.Location ?? throw new InvalidOperationException("Redirect lacks a destination."));
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentEncoding.Count > 0) throw new InvalidOperationException("Compressed responses are not supported.");
            if (response.Content.Headers.ContentLength > policy.MaxBytes) throw new InvalidOperationException("Source exceeds 10 MB.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var block = new byte[81920];
            int count;
            while ((count = await stream.ReadAsync(block, ct)) > 0)
            {
                if (buffer.Length + count > policy.MaxBytes) throw new InvalidOperationException("Source exceeds 10 MB.");
                buffer.Write(block, 0, count);
            }
            return (current, response.Content.Headers.ContentType?.MediaType ?? "", buffer.ToArray());
        }
        throw new InvalidOperationException("Too many redirects.");
    }
}

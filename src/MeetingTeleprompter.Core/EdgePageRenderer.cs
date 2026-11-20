using System.Text;
using Microsoft.Playwright;

namespace MeetingTeleprompter.Core;

public interface IWebPageRenderer
{
    Task<string> RenderAsync(Uri uri, byte[] initialHtml, CancellationToken ct);
}

// An isolated, short-lived Edge process. All page traffic is fulfilled by the safe
// fetcher; the browser itself has a dead proxy and cannot connect directly.
public sealed class EdgePageRenderer : IWebPageRenderer
{
    public static bool AllowsRequest(Uri origin, string url, string method, string resourceType)
    {
        if (method != "GET" || resourceType is not ("document" or "script" or "stylesheet" or "xhr" or "fetch")) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        try { SafeUrlFetcher.ValidateUri(uri); }
        catch (InvalidOperationException) { return false; }
        return uri.GetLeftPart(UriPartial.Authority) == origin.GetLeftPart(UriPartial.Authority);
    }

    public async Task<string> RenderAsync(Uri uri, byte[] initialHtml, CancellationToken ct)
    {
        SafeUrlFetcher.ValidateUri(uri);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        ct = deadline.Token;
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Channel = "msedge", Headless = true, Timeout = 10000,
            Proxy = new() { Server = "http://127.0.0.1:9" },
            Args = ["--disable-background-networking", "--disable-component-update", "--disable-sync",
                "--force-webrtc-ip-handling-policy=disable_non_proxied_udp"]
        });
        ct.ThrowIfCancellationRequested();
        await using var context = await browser.NewContextAsync(new()
        {
            AcceptDownloads = false, ServiceWorkers = ServiceWorkerPolicy.Block,
            Permissions = [], JavaScriptEnabled = true
        });
        context.SetDefaultTimeout(10000);
        await context.RouteWebSocketAsync("**/*", socket => socket.CloseAsync());
        var fetcher = new SafeUrlFetcher(new() { MaxBytes = 4 * 1024 * 1024 });
        var requests = 0;
        long bytes = initialHtml.Length;
        await context.RouteAsync("**/*", async route =>
        {
            try
            {
                var request = route.Request;
                if (ct.IsCancellationRequested || Interlocked.Increment(ref requests) > 40 ||
                    !AllowsRequest(uri, request.Url, request.Method, request.ResourceType))
                { await route.AbortAsync(); return; }
                if (request.IsNavigationRequest && request.Url != uri.AbsoluteUri)
                { await route.AbortAsync(); return; }
                if (request.Url == uri.AbsoluteUri)
                { await route.FulfillAsync(new() { ContentType = "text/html; charset=utf-8", BodyBytes = initialHtml }); return; }
                var response = await fetcher.FetchAsync(new Uri(request.Url), uri, ct);
                if (Interlocked.Add(ref bytes, response.Content.Length) > 10 * 1024 * 1024)
                { await route.AbortAsync(); return; }
                await route.FulfillAsync(new() { ContentType = response.ContentType, BodyBytes = response.Content });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { try { await route.AbortAsync(); } catch (PlaywrightException) { } }
        });
        var page = await context.NewPageAsync();
        page.Dialog += async (_, dialog) => { try { await dialog.DismissAsync(); } catch (PlaywrightException) { } };
        await page.GotoAsync(uri.AbsoluteUri, new() { WaitUntil = WaitUntilState.DOMContentLoaded }).WaitAsync(ct);
        try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 5000 }).WaitAsync(ct); }
        catch (TimeoutException) { /* bounded extraction still works for polling pages */ }
        // Let short client-side rendering settle, without waiting indefinitely for a page.
        await Task.Delay(350, ct);
        var html = await page.ContentAsync().WaitAsync(ct);
        if (Encoding.UTF8.GetByteCount(html) > 10 * 1024 * 1024) throw new InvalidOperationException("Rendered page exceeds 10 MB.");
        return html;
    }
}

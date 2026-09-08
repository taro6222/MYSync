namespace MYSync.Sync.Core;

public sealed class RequestTimeoutHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    public static readonly HttpRequestOptionsKey<TimeSpan> Budget = new("MYSync.RequestTimeout");
    public static readonly HttpRequestOptionsKey<bool> Streaming = new("MYSync.StreamingResponse");
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(request.Options.TryGetValue(Budget, out var duration) ? duration : TimeSpan.FromSeconds(30));
        var response = await base.SendAsync(request, timeout.Token);
        try
        {
            if (!request.Options.TryGetValue(Streaming, out var streaming) || !streaming)
                await response.Content.LoadIntoBufferAsync(4 * 1024 * 1024, timeout.Token);
            return response;
        }
        catch { response.Dispose(); throw; }
    }
}

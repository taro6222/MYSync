using MYSync.Sync.Core;

namespace MYSync.Provider.WebDav;

internal sealed class DiagnosticHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            var response = await base.SendAsync(request, ct);
            SyncDiagnostics.Write("webdav.response", request.Method.Method, (int)response.StatusCode,
                response.Headers.ETag is { IsWeak: false });
            return response;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            SyncDiagnostics.Write("webdav.transport-failure", request.Method.Method, exceptionType: ex.GetType().Name);
            throw;
        }
    }
}

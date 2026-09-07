using System.Net;
using System.Text;
using MYSync.Provider.WebDav;
using MYSync.PluginHost;

internal static class WebDavChecks
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); }
    }
    private static string Item(string href, bool folder = true, string status = "200 OK") => $"<d:response><d:href>{href}</d:href><d:propstat><d:prop><d:resourcetype>{(folder ? "<d:collection/>" : "")}</d:resourcetype></d:prop><d:status>HTTP/1.1 {status}</d:status></d:propstat></d:response>";
    private static string List(params string[] items) => "<d:multistatus xmlns:d=\"DAV:\">" + string.Join("", items) + "</d:multistatus>";
    private static HttpResponseMessage Xml(string body) => new(HttpStatusCode.MultiStatus) { Content = new StringContent(body, Encoding.UTF8, "application/xml") };
    public static async Task Run(string root)
    {
        static void Check(bool ok, string text) { if (!ok) throw new Exception(text); }
        static async Task Reject(Func<Task> action)
        { try { await action(); } catch (Exception ex) when (ex is WebDavException or ArgumentException or System.Xml.XmlException) { return; } throw new Exception("invalid WebDAV input accepted"); }
        var settings = new Dictionary<string,string> { ["url"] = "https://dav.example.test/root/", ["username"] = "test", ["password"] = "test-only" };
        var calls = 0;
        using var provider = new WebDavProvider(() => new Handler(request =>
        {
            calls++;
            Check(request.Method.Method == "PROPFIND" && request.Headers.GetValues("Depth").Single() == "1", "PROPFIND depth");
            Check(request.Headers.Authorization?.Scheme == "Basic", "authentication missing");
            Check(request.RequestUri!.Host == "dav.example.test", "credentials sent outside origin");
            return request.RequestUri.AbsolutePath == "/root/" ? Xml(List(Item("/root/"), Item("/root/%ED%95%9C%EA%B8%80/"), Item("/root/file.txt", false))) : Xml(List(Item(request.RequestUri.AbsolutePath)));
        }));
        await provider.ConnectAsync(settings, default);
        var folders = await provider.GetFoldersAsync(null, default);
        Check(provider.IsConnected && folders.Count == 2 && folders[1].Name == "한글", "folder discovery");
        Check((await provider.GetFoldersAsync(folders[1].Id, default)).Count == 1, "nested folder");
        var before = calls;
        await Reject(() => provider.GetFoldersAsync("https://evil.example/root/", default));
        await Reject(() => provider.GetFoldersAsync("https://dav.example.test/outside/", default));
        await Reject(() => provider.GetFoldersAsync("https://dav.example.test/root/a%2Fb/", default));
        Check(calls == before, "invalid destination reached network");
        await Reject(() => provider.ConnectAsync(new Dictionary<string,string>(settings) { ["url"] = "http://dav.example.test/root/" }, default));
        Check(provider.IsConnected, "failed reconnect discarded valid session");
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound, HttpStatusCode.Redirect })
        {
            using var failed = new WebDavProvider(() => new Handler(_ => new(status)));
            await Reject(() => failed.ConnectAsync(settings, default)); Check(!failed.IsConnected, "failed connection published");
        }
        foreach (var xml in new[]
        {
            List(), List(Item("/root/", false)), List(Item("/root/"), Item("/root/denied/", true, "403 Forbidden")),
            List(Item("/root/"), Item("https://evil.example/root/")), List(Item("/root/"), Item("/root/deep/child/")),
            List(Item("/root/"), Item("/root/")), "<!DOCTYPE a [<!ENTITY x SYSTEM 'file:///C:/none'>]><a>&x;</a>"
        })
        {
            using var failed = new WebDavProvider(() => new Handler(_ => Xml(xml)));
            await Reject(() => failed.ConnectAsync(settings, default));
        }
        Console.WriteLine("PASS: WebDAV auth, nested/unicode folders, scope validation, partial errors, redirects, malformed XML and DTD rejection");
        using var catalog = new PluginCatalog();
        catalog.Load(Path.Combine(root, "src", "MYSync.Desktop", "bin", "Debug", "net10.0-windows", "plugins"));
        Check(catalog.Issues.Count == 0 && catalog.Providers.Any(x => x.Id == "mysync.webdav"), "WebDAV DLL not loadable");
        Console.WriteLine("PASS: WebDAV plugin dynamically loaded alongside sample");
    }
}

using System.Net;
using System.Text.Json;
using MYSync.PluginHost;
using MYSync.Provider.Abstractions;
using MYSync.Provider.GoogleDrive;
using MYSync.Sync.Core;
using MYSync.Sync.Infrastructure;

internal static class GoogleDriveChecks
{
    private sealed class Session : IGoogleSession
    {
        public int Requests;
        public bool Disposed;
        public Task<string> AccessTokenAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); Requests++; return Task.FromResult("access-test-only"); }
        public string ExportToken() => "refresh-test-only";
        public void Dispose() => Disposed = true;
    }
    private sealed class Server : HttpMessageHandler
    {
        public bool Deny, Incomplete, RepeatPage, WrongParent, Shared;
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Requests++;
            if (request.RequestUri!.Host != "www.googleapis.com" || request.Headers.Authorization?.ToString() != "Bearer access-test-only")
                throw new Exception("token sent to wrong origin or bearer missing");
            if (Deny) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            object Folder(string id, string name) => Shared ? new { id, name, mimeType = "application/vnd.google-apps.folder", driveId = "shared" } :
                (object)new { id, name, mimeType = "application/vnd.google-apps.folder", trashed = false };
            var uri = request.RequestUri;
            object data;
            if (uri.AbsolutePath.EndsWith("/files/root") || uri.AbsolutePath.EndsWith("/files/myroot")) data = Folder("myroot", "내 드라이브");
            else if (uri.AbsolutePath.EndsWith("/files/a")) data = Folder("a", "한글 폴더");
            else if (uri.AbsolutePath.EndsWith("/files"))
            {
                var query = Uri.UnescapeDataString(uri.Query);
                if (!query.Contains("'myroot' in parents") || !query.Contains("trashed=false")) throw new Exception("folder query missing scope");
                var second = query.Contains("pageToken=next");
                data = new { incompleteSearch = Incomplete, nextPageToken = !second || RepeatPage ? "next" : null,
                    files = new[] { new { id = second ? "b" : "a", name = "한글 폴더", mimeType = "application/vnd.google-apps.folder", trashed = false,
                        parents = new[] { WrongParent ? "elsewhere" : "myroot" } } } };
            }
            else throw new Exception("unexpected endpoint");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(data)) });
        }
    }
    public static async Task Run(string root, string scratch)
    {
        static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        static async Task Reject(Func<Task> action)
        {
            try { await action(); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException or OperationCanceledException) { return; }
            throw new Exception("invalid Google response accepted");
        }
        var server = new Server(); var sessions = new List<Session>();
        var configPath = Path.Combine(scratch, "oauth-config.json");
        try { OAuthClientConfiguration.Load(configPath); throw new Exception("missing app config accepted"); }
        catch (InvalidOperationException) { }
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new { installed = new { client_id = "app.apps.googleusercontent.com", client_secret = "test-client" } }));
        Check(OAuthClientConfiguration.Load(configPath)["client_id"] == "app.apps.googleusercontent.com", "desktop client config not loaded");
        await File.WriteAllTextAsync(configPath, "{\"web\":{}}");
        try { OAuthClientConfiguration.Load(configPath); throw new Exception("web client accepted as desktop"); }
        catch (InvalidOperationException) { }
        using var provider = new GoogleDriveProvider((values, ct) =>
        { ct.ThrowIfCancellationRequested(); var session = new Session(); sessions.Add(session); return Task.FromResult<IGoogleSession>(session); }, () => server);
        var values = new Dictionary<string, string> { ["client_id"] = "test.apps.googleusercontent.com", ["client_secret"] = "client-secret-test-only" };
        await provider.ConnectAsync(values, default);
        Check(provider is IBrowserLoginProvider && provider.ConnectionFields.Count == 0 && provider.LoginButtonText == "Google로 로그인", "Google still exposes credential entry fields");
        var folders = await provider.GetFoldersAsync(null, default);
        Check(folders.Count == 3 && folders[0].Id == "myroot" && folders.Skip(1).Select(x => x.Id).SequenceEqual(["a", "b"]), "pagination/root order/stable duplicate IDs");
        Check(sessions[0].Requests == server.Requests, "token not obtained for each API request");
        await Reject(() => provider.GetFoldersAsync("undiscovered", default));
        server.Incomplete = true; await Reject(() => provider.GetFoldersAsync(null, default)); server.Incomplete = false;
        server.RepeatPage = true; await Reject(() => provider.GetFoldersAsync(null, default)); server.RepeatPage = false;
        server.WrongParent = true; await Reject(() => provider.GetFoldersAsync(null, default)); server.WrongParent = false;
        server.Shared = true; await Reject(() => provider.GetFoldersAsync(null, default)); server.Shared = false;
        server.Deny = true;
        await Reject(() => provider.ConnectAsync(values, default));
        Check(provider.IsConnected && !sessions[0].Disposed && sessions[1].Disposed, "failed connection replaced good session or leaked candidate");
        server.Deny = false;
        using (var cts = new CancellationTokenSource())
        { cts.Cancel(); await Reject(() => provider.GetFoldersAsync(null, cts.Token)); }
        var store = new AccountStore(Path.Combine(scratch, "google-account.db"));
        var account = new SavedAccount(Guid.NewGuid(), provider.Id, "Google test");
        store.Save(account, provider.ExportConnectionValues());
        var restored = store.ReadValues(account);
        Check(restored["oauth_token"] == "refresh-test-only" && restored["client_secret"] == values["client_secret"], "generated OAuth tokens not persisted");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var bytes = await File.ReadAllBytesAsync(Path.Combine(scratch, "google-account.db"));
        Check(!System.Text.Encoding.UTF8.GetString(bytes).Contains("refresh-test-only"), "refresh token persisted in plaintext");

        // Exercise SDK dependency loading in the isolated plugin context without opening a browser or contacting Google.
        using var catalog = new PluginCatalog();
        catalog.Load(Path.Combine(root, "src", "MYSync.Desktop", "bin", "Debug", "net10.0-windows", "plugins"));
        var loaded = catalog.Providers.Single(x => x.Id == provider.Id);
        Check(loaded is IPersistableConnectionProvider && loaded is ITransferProvider, "shared optional contract or capability incorrect");
        var invalid = new Dictionary<string, string>(values) { ["oauth_token"] = "{" };
        await Reject(() => ((IConfigurableProvider)loaded).ConnectAsync(invalid, default));
        try { await ((IConfigurableProvider)loaded).ConnectAsync(new Dictionary<string, string>(values) { ["oauth_token"] = "old-token", ["oauth_scope"] = "https://www.googleapis.com/auth/drive.readonly" }, default); throw new Exception("read-only account allowed transfers"); }
        catch (SyncTransferException ex) when (ex.Kind == SyncFailureKind.Authentication) { }
        Console.WriteLine("PASS: Google folder pagination/IDs, incomplete/cross-parent/shared results rejected, cancellation, failed-session isolation, encrypted OAuth export, SDK plugin loading");
    }
}

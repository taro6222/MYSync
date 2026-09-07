using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using MYSync.Provider.Abstractions;
using MYSync.Provider.WebDav;
using MYSync.Sync.Core;
using MYSync.Sync.Infrastructure;

internal static class WebDavTransferChecks
{
    private sealed record Resource(byte[]? Data, string Tag);
    private sealed class Server : HttpMessageHandler
    {
        public readonly Dictionary<string, Resource> Files = new(StringComparer.Ordinal);
        public bool ChangeBeforeMove;
        public bool WeakTags;
        public bool FailListing;
        public bool FailAfterMove;
        public int Moves;
        private int version;
        public Server() => Folder("/root");
        public void Seed(string path, string text) => Files[path] = new(Encoding.UTF8.GetBytes(text), $"\"{++version}\"");
        public void Folder(string path) => Files[path] = new(null, $"\"{++version}\"");
        private static HttpResponseMessage Reply(HttpStatusCode status) => new(status);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath).TrimEnd('/');
            Files.TryGetValue(path, out var current);
            if (request.Headers.IfMatch.Count > 0 && request.Headers.IfMatch.Single().ToString() != current?.Tag) return Reply(HttpStatusCode.PreconditionFailed);
            switch (request.Method.Method)
            {
                case "PROPFIND":
                    if (FailListing) return Reply(HttpStatusCode.Forbidden);
                    if (current is null) return Reply(HttpStatusCode.NotFound);
                    XNamespace d = "DAV:";
                    var items = Files.Where(x => x.Key == path || x.Key.StartsWith(path + "/", StringComparison.Ordinal) && !x.Key[(path.Length + 1)..].Contains('/'));
                    var xml = new XElement(d + "multistatus", items.Select(x => new XElement(d + "response",
                        new XElement(d + "href", x.Key + (x.Value.Data is null ? "/" : "")),
                        new XElement(d + "propstat", new XElement(d + "prop", new XElement(d + "resourcetype", x.Value.Data is null ? new XElement(d + "collection") : null)), new XElement(d + "status", "HTTP/1.1 200 OK")))));
                    return new(HttpStatusCode.MultiStatus) { Content = new StringContent(xml.ToString()) };
                case "GET":
                    if (current?.Data is null) return Reply(HttpStatusCode.NotFound);
                    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(current.Data) };
                    response.Headers.ETag = EntityTagHeaderValue.Parse((WeakTags ? "W/" : "") + current.Tag); return response;
                case "PUT":
                    if (request.Headers.IfNoneMatch.SingleOrDefault()?.ToString() != "*" || current is not null) return Reply(HttpStatusCode.PreconditionFailed);
                    Files[path] = new(await request.Content!.ReadAsByteArrayAsync(ct), $"\"{++version}\""); return Reply(HttpStatusCode.Created);
                case "MKCOL":
                    if (current is not null) return Reply(HttpStatusCode.MethodNotAllowed);
                    Folder(path); return Reply(HttpStatusCode.Created);
                case "MOVE":
                    var destination = Uri.UnescapeDataString(new Uri(request.Headers.GetValues("Destination").Single()).AbsolutePath).TrimEnd('/');
                    if (ChangeBeforeMove) { ChangeBeforeMove = false; Seed(destination, "external-edit"); }
                    Files.TryGetValue(destination, out var old);
                    if (old is not null && request.Headers.GetValues("Overwrite").Single() == "F") return Reply(HttpStatusCode.PreconditionFailed);
                    if (old is not null && (!request.Headers.TryGetValues("If", out var conditions) || !conditions.Single().Contains("([" + old.Tag + "])", StringComparison.Ordinal))) return Reply(HttpStatusCode.PreconditionFailed);
                    if (current is null) return Reply(HttpStatusCode.NotFound);
                    Files[destination] = current; Files.Remove(path); Moves++;
                    if (FailAfterMove) { FailAfterMove = false; throw new HttpRequestException("simulated lost MOVE response"); }
                    return Reply(old is null ? HttpStatusCode.Created : HttpStatusCode.NoContent);
                case "DELETE":
                    if (current is null) return Reply(HttpStatusCode.NotFound);
                    if (current.Data is null) throw new Exception("recursive delete must never be sent");
                    Files.Remove(path); return Reply(HttpStatusCode.NoContent);
                default: throw new Exception("unexpected method");
            }
        }
    }
    public static async Task Run(string scratch)
    {
        static void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); }
        var server = new Server(); server.Seed("/root/cloud.txt", "cloud"); server.Folder("/root/폴더"); server.Seed("/root/폴더/파일.txt", "nested");
        using var provider = new WebDavProvider(() => server);
        await provider.ConnectAsync(new Dictionary<string,string> { ["url"] = "https://dav.test/root/", ["username"] = "test", ["password"] = "test-only" }, default);
        var remote = ((ITransferProvider)provider).OpenEndpoint("https://dav.test/root/");
        var area = Path.Combine(scratch, "webdav-transfer"); var localRoot = Path.Combine(area, "local"); Directory.CreateDirectory(localRoot);
        await File.WriteAllTextAsync(Path.Combine(localRoot, "local.txt"), "local");
        var local = new LocalEndpoint(localRoot, Path.Combine(area, "recovery"));
        var journal = new SyncJournal(Path.Combine(area, "journal.db")); var pair = Guid.NewGuid(); var executor = new SyncExecutor(journal);
        async Task<ExecutionReport> Sync()
        {
            journal.Enqueue(pair, SyncPlanner.Compare(await local.ScanAsync(default), await remote.ScanAsync(default), journal.ReadBaseline(pair)));
            return await executor.RunAsync(pair, local, remote);
        }
        var initial = await Sync(); Check(initial.Converged, string.Join(" / ", initial.Issues));
        Check(await File.ReadAllTextAsync(Path.Combine(localRoot, "폴더", "파일.txt")) == "nested", "recursive download");
        Check(Encoding.UTF8.GetString(server.Files["/root/local.txt"].Data!) == "local", "upload content");
        await File.WriteAllTextAsync(Path.Combine(localRoot, "local.txt"), "update"); Check((await Sync()).Converged, "conditional overwrite");
        File.Delete(Path.Combine(localRoot, "local.txt")); Check((await Sync()).Converged && !server.Files.ContainsKey("/root/local.txt"), "conditional delete");
        Check(!server.Files.Keys.Any(x => x.Contains(".mysync-upload-", StringComparison.Ordinal)), "staging leak after success");
        Console.WriteLine("PASS: WebDAV HTTP model + real local folder recursive roundtrip, conditional overwrite and file deletion");

        var snapshot = await remote.ScanAsync(default); var expected = snapshot.Entries.Single(x => x.Path == "cloud.txt");
        static MemoryStream Data(string text) => new(Encoding.UTF8.GetBytes(text));
        static string Hash(string text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        server.ChangeBeforeMove = true;
        try { await remote.PutFileAsync("cloud.txt", expected, Data("replacement"), Hash("replacement"), default); throw new Exception("stale MOVE accepted"); } catch (WebDavException ex) when (ex.Status == HttpStatusCode.PreconditionFailed) { }
        Check(Encoding.UTF8.GetString(server.Files["/root/cloud.txt"].Data!) == "external-edit", "external edit lost");
        Check(!server.Files.Keys.Any(x => x.Contains(".mysync-upload-", StringComparison.Ordinal)), "verified temporary cleanup failed");
        server.WeakTags = true; expected = (await remote.ScanAsync(default)).Entries.Single(x => x.Path == "cloud.txt");
        try { await remote.DeleteAsync(expected, default); throw new Exception("weak ETag deletion accepted"); } catch (SyncPreconditionException) { }
        server.WeakTags = false;
        try { await remote.DeleteAsync(new("폴더", EntryKind.Directory, null), default); throw new Exception("folder DELETE accepted"); } catch (SyncPreconditionException) { }
        server.FailListing = true; Check(!(await remote.ScanAsync(default)).IsComplete, "failed listing treated as empty"); server.FailListing = false;
        Console.WriteLine("PASS: destination race preserves external edit, temporary cleanup, weak ETag and folder delete rejection");

        // A new pair isolates response-loss recovery from the intentionally changed prior pair.
        var retryPair = Guid.NewGuid(); journal.CommitConverged(retryPair, await remote.ScanAsync(default), await remote.ScanAsync(default));
        var source = new MemoryEndpoint(); source.Seed("new.txt", "retry");
        var one = (await source.ScanAsync(default)).Entries.Single();
        journal.Enqueue(retryPair, new([new("new.txt", SyncAction.Upload, one, null, "test")], []));
        server.FailAfterMove = true;
        await executor.RunAsync(retryPair, source, remote); var moves = server.Moves;
        await executor.RunAsync(retryPair, source, remote);
        Check(server.Moves == moves && journal.ReadJobs(retryPair).Single().State == JobState.Applied, "uncertain upload replayed");
        Console.WriteLine("PASS: lost MOVE response reconciled without repeating upload");
    }
}

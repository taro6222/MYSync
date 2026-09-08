using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MYSync.Provider.GoogleDrive;
using MYSync.Sync.Core;
using MYSync.Sync.Infrastructure;

internal static class GoogleTransferChecks
{
    private const string Folder = "application/vnd.google-apps.folder";
    private sealed class Session : IGoogleSession
    {
        public Task<string> AccessTokenAsync(CancellationToken ct) => Task.FromResult("test-token");
        public string ExportToken() => "test-refresh";
        public void Dispose() { }
    }
    private sealed record Resource(string Id, string Name, string Parent, string Mime, byte[] Data, int Version, bool Trashed = false);
    private sealed class Server : HttpMessageHandler
    {
        public readonly System.Collections.Concurrent.ConcurrentDictionary<string, Resource> Items = new();
        public int Writes, Trashes, MediaReads;
        public bool Checksums;
        public bool Race, LoseResponse, NoTag, NoV2Tag, Incomplete;
        private int ids = 10;
        private readonly Dictionary<string, (Resource Item, MemoryStream Data, long Size)> uploads = [];
        public int Chunks, LegacyUploads;
        private static void CheckLegacyMethod(HttpRequestMessage request)
        { if (request.Method != HttpMethod.Put) throw new Exception("v2 upload must use PUT"); }
        public Server() => Items["root"] = new("root", "내 드라이브", "", Folder, [], 1);
        public void Seed(string id, string name, string parent, string content, string mime = "application/octet-stream") =>
            Items[id] = new(id, name, parent, mime, Encoding.UTF8.GetBytes(content), Items.TryGetValue(id, out var old) ? old.Version + 1 : 1);
        private object Model(Resource r) => new { id = r.Id, name = r.Name, parents = r.Parent == "" ? Array.Empty<string>() : new[] { r.Parent }, mimeType = r.Mime,
            version = r.Version.ToString(), size = r.Data.Length.ToString(), trashed = r.Trashed, sha256Checksum = Checksums ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(r.Data)) : null };
        private static HttpResponseMessage Json(object data) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(data)) };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (request.RequestUri!.Host != "www.googleapis.com" || request.Headers.Authorization?.Parameter != "test-token") throw new Exception("bad Google request origin/auth");
            var path = request.RequestUri.AbsolutePath; var query = Uri.UnescapeDataString(request.RequestUri.Query);
            if (path.EndsWith("/generateIds")) return Json(new { ids = new[] { "generated" + Interlocked.Increment(ref ids) } });
            if (request.Method == HttpMethod.Get && path.EndsWith("/files"))
            {
                var start = query.IndexOf("q='") + 3; var end = query.IndexOf("' in parents", start); var parent = query[start..end];
                return Json(new { incompleteSearch = Incomplete, files = Items.Values.Where(x => x.Parent == parent && !x.Trashed).Select(Model).ToArray() });
            }
            if (query.Contains("uploadType=resumable"))
            {
                using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var create = request.Method == HttpMethod.Post;
                if (path.Contains("/v2/")) { CheckLegacyMethod(request); LegacyUploads++; }
                var uploadId = create ? doc.RootElement.GetProperty("id").GetString()! : path.Split('/').Last();
                var resource = create ? new Resource(uploadId, doc.RootElement.GetProperty("name").GetString()!, doc.RootElement.GetProperty("parents")[0].GetString()!, "application/octet-stream", [], 0) : Items[uploadId];
                if (!create && request.Headers.IfMatch.SingleOrDefault()?.ToString() != $"\"v{resource.Version}\"") return new(HttpStatusCode.PreconditionFailed);
                uploads[uploadId] = (resource, new MemoryStream(), long.Parse(request.Headers.GetValues("X-Upload-Content-Length").Single()));
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.Location = new Uri("https://www.googleapis.com/upload/drive/" + (path.Contains("/v2/") ? "v2" : "v3") + "/files?upload_id=" + uploadId);
                return response;
            }
            if (request.Method == HttpMethod.Put && query.StartsWith("?upload_id="))
            {
                var uploadId = query["?upload_id=".Length..]; var upload = uploads[uploadId];
                var range = request.Content!.Headers.ContentRange!;
                if (range.From != upload.Data.Length || range.Length != upload.Size) throw new Exception("incorrect chunk range");
                await request.Content.CopyToAsync(upload.Data, ct); Chunks++;
                if (upload.Data.Length < upload.Size)
                {
                    var response = new HttpResponseMessage((HttpStatusCode)308);
                    response.Headers.TryAddWithoutValidation("Range", "bytes=0-" + (upload.Data.Length - 1)); return response;
                }
                Items[uploadId] = upload.Item with { Data = upload.Data.ToArray(), Version = upload.Item.Version + 1 };
                upload.Data.Dispose(); uploads.Remove(uploadId); Writes++; return Json(new { id = uploadId });
            }
            var id = path.Split('/').Last(); Items.TryGetValue(id, out var current);
            if (request.Method == HttpMethod.Get)
            {
                if (current is null) return new(HttpStatusCode.NotFound);
                if (query.Contains("alt=media")) { MediaReads++; return new(HttpStatusCode.OK) { Content = new ByteArrayContent(current.Data) }; }
                if (path.Contains("/v2/")) return Json(new { id = current.Id, version = current.Version.ToString(), etag = NoV2Tag ? null : $"\"v{current.Version}\"" });
                var result = Json(Model(current));
                if (!NoTag) result.Headers.ETag = new EntityTagHeaderValue($"\"v{current.Version}\"");
                return result;
            }
            if (Race && current is not null && (request.Method == HttpMethod.Patch || request.Method == HttpMethod.Put))
            { Race = false; Seed(id, current.Name, current.Parent, "external-change"); current = Items[id]; }
            if ((request.Method == HttpMethod.Patch || request.Method == HttpMethod.Put) && request.Headers.IfMatch.SingleOrDefault()?.ToString() != $"\"v{current?.Version}\"") return new(HttpStatusCode.PreconditionFailed);
            if (request.Content is MultipartContent multipart)
            {
                var parts = multipart.ToArray(); using var metadata = JsonDocument.Parse(await parts[0].ReadAsStringAsync(ct));
                var body = metadata.RootElement;
                if (request.Method == HttpMethod.Post)
                {
                    id = body.GetProperty("id").GetString()!;
                    if (Items.ContainsKey(id)) return new(HttpStatusCode.Conflict);
                    current = new(id, body.GetProperty("name").GetString()!, body.GetProperty("parents")[0].GetString()!, "application/octet-stream", [], 0);
                }
                if (current is null) return new(HttpStatusCode.NotFound);
                Items[id] = current with { Data = await parts[1].ReadAsByteArrayAsync(ct), Version = current.Version + 1 }; Writes++;
                if (LoseResponse) { LoseResponse = false; throw new HttpRequestException("lost Google upload response"); }
                return Json(new { id });
            }
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            if (request.Method == HttpMethod.Post)
            {
                var value = json.RootElement; id = value.GetProperty("id").GetString()!;
                Items[id] = new(id, value.GetProperty("name").GetString()!, value.GetProperty("parents")[0].GetString()!, Folder, [], 1);
                return Json(new { id });
            }
            if (request.Method == HttpMethod.Patch && current is not null && (path.Contains("/v2/") ? json.RootElement.GetProperty("labels") : json.RootElement).GetProperty("trashed").GetBoolean())
            { Items[id] = current with { Trashed = true, Version = current.Version + 1 }; Trashes++; return Json(new { id, trashed = true }); }
            throw new Exception("unexpected Google request");
        }
    }
    public static async Task Run(string scratch)
    {
        static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        static async Task Reject(Func<Task> action)
        { try { await action(); } catch (SyncTransferException) { return; } throw new Exception("unsafe Google mutation accepted"); }
        static MemoryStream Data(string text) => new(Encoding.UTF8.GetBytes(text));
        static string Hash(string text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        var server = new Server(); server.Seed("cloud", "cloud.txt", "root", "cloud");
        using var provider = new GoogleDriveProvider((_, _) => Task.FromResult<IGoogleSession>(new Session()), () => server);
        await provider.ConnectAsync(new Dictionary<string, string> { ["client_id"] = "test.apps.googleusercontent.com", ["client_secret"] = "test" }, default);
        var remote = provider.OpenEndpoint("root");
        server.Seed("excluded", "secret.bak", "root", new string('x', 32768));
        ((ISyncPolicyEndpoint)remote).Policy = new("*.bak", 64);
        var filtered = await remote.ScanAsync(default);
        Check(filtered.IsComplete && filtered.Entries.All(x => x.Path != "secret.bak") && filtered.Unsupported.Any(x => x.Path == "secret.bak"), "Google exclusion failed");
        ((ISyncPolicyEndpoint)remote).Policy = new("", 64);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        await remote.ScanAsync(default);
        Check(timer.Elapsed >= TimeSpan.FromMilliseconds(450), "Google HTTP download bypassed bandwidth limit");
        ((ISyncPolicyEndpoint)remote).Policy = new();
        server.Items.TryRemove("excluded", out _);
        server.Checksums = true; var mediaReads = server.MediaReads;
        var metadataScan = await remote.ScanAsync(default);
        Check(metadataScan.IsComplete && metadataScan.Entries.Single(x => x.Path == "cloud.txt").ContentHash == Hash("cloud") &&
            server.MediaReads == mediaReads, "server SHA256 scan downloaded unchanged content");
        server.Checksums = false;
        var area = Path.Combine(scratch, "google-transfer"); var localRoot = Path.Combine(area, "local"); Directory.CreateDirectory(Path.Combine(localRoot, "한글"));
        await File.WriteAllTextAsync(Path.Combine(localRoot, "한글", "local.txt"), "local");
        var local = new LocalEndpoint(localRoot, Path.Combine(area, "recovery")); var pair = Guid.NewGuid();
        var journal = new SyncJournal(Path.Combine(area, "journal.db"));
        var executor = new SyncExecutor(journal, new RetryPolicy(2, TimeSpan.Zero, TimeSpan.Zero));
        async Task<ExecutionReport> Sync()
        {
            journal.Enqueue(pair, SyncPlanner.Compare(await local.ScanAsync(default), await remote.ScanAsync(default), journal.ReadBaseline(pair)));
            return await executor.RunAsync(pair, local, remote);
        }
        var first = await Sync(); Check(first.Converged, "Google initial roundtrip failed: " + string.Join(" / ", first.Issues));
        Check(await File.ReadAllTextAsync(Path.Combine(localRoot, "cloud.txt")) == "cloud", "Google download missing");
        await File.WriteAllTextAsync(Path.Combine(localRoot, "한글", "local.txt"), "updated");
        Check((await Sync()).Converged, "Google conditional update failed");
        File.Delete(Path.Combine(localRoot, "한글", "local.txt"));
        Check((await Sync()).Converged && server.Trashes == 1, "Google trash propagation failed");
        var expected = (await remote.ScanAsync(default)).Entries.Single(x => x.Path == "cloud.txt");
        server.Race = true;
        await Reject(() => remote.PutFileAsync("cloud.txt", expected, Data("replacement"), Hash("replacement"), default));
        Check(Encoding.UTF8.GetString(server.Items["cloud"].Data) == "external-change", "Google concurrent content overwritten");
        expected = (await remote.ScanAsync(default)).Entries.Single(x => x.Path == "cloud.txt");
        server.NoTag = true;
        await remote.PutFileAsync("cloud.txt", expected, Data("v2-replacement"), Hash("v2-replacement"), default);
        Check(Encoding.UTF8.GetString(server.Items["cloud"].Data) == "v2-replacement", "v3 missing ETag did not use v2 conditional overwrite");
        expected = (await remote.ScanAsync(default)).Entries.Single(x => x.Path == "cloud.txt");
        server.Race = true;
        await Reject(() => remote.PutFileAsync("cloud.txt", expected, Data("race"), Hash("race"), default));
        Check(Encoding.UTF8.GetString(server.Items["cloud"].Data) == "external-change", "v2 conditional race lost external edit");
        expected = (await remote.ScanAsync(default)).Entries.Single(x => x.Path == "cloud.txt");
        server.NoV2Tag = true; await Reject(() => remote.DeleteAsync(expected, default)); server.NoV2Tag = false;
        await remote.DeleteAsync(expected, default);
        Check(server.Items["cloud"].Trashed, "v2 conditional trash failed");
        server.NoTag = false;
        await Reject(() => remote.DeleteAsync(new("한글", EntryKind.Directory, null), default));
        server.Seed("doc", "문서", "root", "", "application/vnd.google-apps.document");
        server.Seed("dupe1", "duplicate", "root", "one"); server.Seed("dupe2", "duplicate", "root", "two");
        var skipped = await remote.ScanAsync(default);
        Check(skipped.IsComplete && skipped.Unsupported.Count == 2 && skipped.Entries.All(x => x.Path != "duplicate"), "Google unsupported/duplicate reporting failed");
        server.Incomplete = true; Check(!(await remote.ScanAsync(default)).IsComplete, "incomplete Google scan accepted"); server.Incomplete = false;
        var bigPayload = new string('L', 9 * 1024 * 1024 + 17);
        await remote.PutFileAsync("big.dat", null, Data(bigPayload), Hash(bigPayload), default);
        Check(server.Chunks == 2, "large upload did not use bounded chunks");
        var bigEntry = (await remote.ScanAsync(default)).Entries.Single(x => x.Path == "big.dat");
        await using (var bigRead = await remote.OpenReadAsync(bigEntry, default))
            Check(bigRead.Length == bigPayload.Length, "large download truncated");
        server.NoTag = true;
        await remote.PutFileAsync("big.dat", bigEntry, Data(bigPayload + "updated"), Hash(bigPayload + "updated"), default);
        Check(server.LegacyUploads == 1 && server.Chunks == 4, "large no-ETag overwrite did not use v2 resumable update");
        server.NoTag = false;
        var writes = server.Writes; using var large = new MemoryStream(new byte[5 * 1024 * 1024 + 1]);
        await Reject(() => remote.PutFileAsync("large.bin", null, large, "unused", default)); Check(server.Writes == writes, "oversized upload mutated server");

        var source = new MemoryEndpoint(); source.Seed("lost.txt", "lost"); var entry = (await source.ScanAsync(default)).Entries.Single();
        var retryPair = Guid.NewGuid(); journal.Enqueue(retryPair, new([new("lost.txt", SyncAction.Upload, entry, null, "test")], []));
        server.LoseResponse = true; writes = server.Writes;
        await executor.RunAsync(retryPair, source, remote);
        Check(server.Writes == writes + 1 && journal.ReadJobs(retryPair).Single().State == JobState.Completed, "Google response loss repeated upload");
        Console.WriteLine("PASS: Google recursive disk roundtrip/update/trash, ETag race and missing-tag protection, unsupported/duplicates, incomplete scans, large chunks and response-loss reconciliation");
    }
}

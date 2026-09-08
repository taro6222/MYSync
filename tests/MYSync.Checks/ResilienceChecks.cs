using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using MYSync.Provider.Abstractions;
using MYSync.Provider.WebDav;
using MYSync.Sync.Core;
using MYSync.Sync.Infrastructure;

internal static class ResilienceChecks
{
    private sealed class Faulty(ISyncEndpoint inner, Func<Exception?> fault) : ISyncEndpoint
    {
        public int Writes;
        public Task<ScanResult> ScanAsync(CancellationToken ct) => inner.ScanAsync(ct);
        public Task<Stream> OpenReadAsync(SyncEntry expected, CancellationToken ct) => inner.OpenReadAsync(expected, ct);
        public Task PutFileAsync(string path, SyncEntry? expected, Stream content, string sha256, CancellationToken ct)
        {
            Writes++;
            return fault() is { } failure ? Task.FromException(failure) : inner.PutFileAsync(path, expected, content, sha256, ct);
        }
        public Task CreateDirectoryAsync(string path, CancellationToken ct) => inner.CreateDirectoryAsync(path, ct);
        public Task DeleteAsync(SyncEntry expected, CancellationToken ct) => inner.DeleteAsync(expected, ct);
    }

    /// <summary>Serves one folder with one file and reports a strong ETag, so repeat scans can reuse the hash.</summary>
    private sealed class TagServer : HttpMessageHandler
    {
        public byte[] Data = Encoding.UTF8.GetBytes("first");
        public string Tag = "\"v1\"";
        public int Gets;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath).TrimEnd('/');
            XNamespace d = "DAV:";
            switch (request.Method.Method)
            {
                case "PROPFIND":
                    if (path != "/root") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                    var xml = new XElement(d + "multistatus",
                        new XElement(d + "response", new XElement(d + "href", "/root/"),
                            new XElement(d + "propstat",
                                new XElement(d + "prop", new XElement(d + "resourcetype", new XElement(d + "collection"))),
                                new XElement(d + "status", "HTTP/1.1 200 OK"))),
                        new XElement(d + "response", new XElement(d + "href", "/root/a.txt"),
                            new XElement(d + "propstat",
                                new XElement(d + "prop",
                                    new XElement(d + "resourcetype"),
                                    new XElement(d + "getetag", Tag),
                                    new XElement(d + "getcontentlength", Data.Length)),
                                new XElement(d + "status", "HTTP/1.1 200 OK"))));
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MultiStatus) { Content = new StringContent(xml.ToString()) });
                case "GET":
                    if (path != "/root/a.txt") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                    Gets++;
                    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Data) };
                    response.Headers.ETag = EntityTagHeaderValue.Parse(Tag);
                    return Task.FromResult(response);
                default:
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));
            }
        }
    }

    public static async Task Run(string scratch)
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        static async Task Reject(Func<Task> action, string message)
        { try { await action(); } catch (IOException) { return; } throw new Exception(message); }

        Check(new WebDavException("x", HttpStatusCode.Unauthorized).Kind == SyncFailureKind.Authentication, "401 not classified as authentication");
        Check(new WebDavException("x", HttpStatusCode.Forbidden).Kind == SyncFailureKind.Permission, "403 not classified as permission");
        Check(new WebDavException("x", HttpStatusCode.NotFound).Kind == SyncFailureKind.Missing, "404 not classified as missing");
        Check(new WebDavException("x", HttpStatusCode.PreconditionFailed).Kind == SyncFailureKind.Precondition, "412 not classified as precondition");
        Check(new WebDavException("x", HttpStatusCode.ServiceUnavailable).Kind == SyncFailureKind.Transient, "503 not classified as transient");
        Check(new WebDavException("x", HttpStatusCode.InternalServerError).Kind == SyncFailureKind.Transient, "500 not classified as transient");
        Check(new WebDavException("x", HttpStatusCode.InsufficientStorage).Kind == SyncFailureKind.Unknown, "507 must not be retried as transient");
        Check(new WebDavException("x").Kind == SyncFailureKind.Unknown, "missing status must not be retried");
        Check(new SyncPreconditionException("x").Kind == SyncFailureKind.Precondition, "precondition failure misclassified");
        var policy = new RetryPolicy(4, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));
        Check(policy.DelayFor(1) == TimeSpan.FromSeconds(1) && policy.DelayFor(2) == TimeSpan.FromSeconds(2) && policy.DelayFor(9) == TimeSpan.FromSeconds(3), "backoff does not grow and cap");
        Console.WriteLine("PASS: failure classification by status and capped exponential backoff");

        var fast = new RetryPolicy(3, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2));
        async Task<(ExecutionReport Report, int Writes)> Transfer(string name, Func<int, Exception?> fault, RetryPolicy retry)
        {
            var area = Path.Combine(scratch, "resilience", name);
            var leftRoot = Path.Combine(area, "left"); var rightRoot = Path.Combine(area, "right");
            Directory.CreateDirectory(leftRoot); Directory.CreateDirectory(rightRoot);
            await File.WriteAllTextAsync(Path.Combine(leftRoot, "f.txt"), "payload");
            var left = new LocalEndpoint(leftRoot, Path.Combine(area, "left-recovery"));
            var attempts = 0;
            var right = new Faulty(new LocalEndpoint(rightRoot, Path.Combine(area, "right-recovery")), () => fault(++attempts));
            var journal = new SyncJournal(Path.Combine(area, "journal.db"));
            var pair = Guid.NewGuid();
            journal.Enqueue(pair, SyncPlanner.Compare(await left.ScanAsync(default), await right.ScanAsync(default), []));
            var report = await new SyncExecutor(journal, retry).RunAsync(pair, left, right);
            var stored = new SyncJournal(Path.Combine(area, "journal.db")).ReadJobs(pair).Single();
            Check(report.Converged ? stored.FailureReason is null : stored.FailureReason is not null && stored.FailureKind is not null && stored.FailedAt is not null,
                "executor did not persist the failure outcome correctly");
            return (report, right.Writes);
        }

        var recovered = await Transfer("transient", n => n <= 2 ? new SyncTransferException("서버가 일시적으로 응답하지 않습니다.", SyncFailureKind.Transient) : null, fast);
        Check(recovered.Report.Converged && recovered.Writes == 3, "transient failure was not retried until it succeeded");

        var exhausted = await Transfer("exhausted", _ => new SyncTransferException("계속 실패", SyncFailureKind.Transient), new RetryPolicy(2, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)));
        Check(!exhausted.Report.Converged && exhausted.Writes == 2, "retry count did not follow the policy");
        Check(exhausted.Report.TransientOnly, "exhausted transient failure was not reported as transient");

        var refused = await Transfer("permanent", _ => new SyncTransferException("권한이 없습니다.", SyncFailureKind.Permission), fast);
        Check(!refused.Report.Converged && refused.Writes == 1, "a permanent failure was retried");
        Check(!refused.Report.TransientOnly && refused.Report.Issues.Single().Contains("[권한]"), "permanent failure not labelled or wrongly marked transient");
        Console.WriteLine("PASS: only transient failures retried, bounded by policy, and classified in the report");

        var monitorRoot = Path.Combine(scratch, "resilience", "monitor"); Directory.CreateDirectory(monitorRoot);
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cycles = 0;
        await using (var monitor = new SyncMonitor(monitorRoot, _ =>
        {
            if (Interlocked.Increment(ref cycles) < 3) return Task.FromResult(new ExecutionReport(false, ["일시 오류"], true));
            settled.TrySetResult();
            return Task.FromResult(new ExecutionReport(true, []));
        }, TimeSpan.FromHours(1), TimeSpan.Zero, 5, TimeSpan.FromMilliseconds(1)))
        {
            monitor.Start();
            await settled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(cycles >= 3, "monitor stopped instead of retrying a transient failure");
        }
        var continued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryCycles = 0;
        await using (var keepEnabled = new SyncMonitor(monitorRoot, _ =>
        {
            if (Interlocked.Increment(ref retryCycles) >= 4)
            { continued.TrySetResult(); return Task.FromResult(new ExecutionReport(true, [])); }
            throw new HttpRequestException("offline");
        }, TimeSpan.FromMilliseconds(40), TimeSpan.Zero, 2, TimeSpan.FromMilliseconds(1)))
        {
            keepEnabled.Start();
            await continued.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!keepEnabled.Completion.IsCompleted && retryCycles >= 4, "monitor disabled after transient retry budget");
        }
        Console.WriteLine("PASS: transient thrown connection errors recover after bounded fast retries and periodic waiting without disabling auto sync");

        var cacheArea = Path.Combine(scratch, "resilience", "local-cache");
        var cacheRoot = Path.Combine(cacheArea, "root"); Directory.CreateDirectory(cacheRoot);
        var file = Path.Combine(cacheRoot, "same-size.txt");
        await File.WriteAllTextAsync(file, "aaaa");
        var cached = new LocalEndpoint(cacheRoot, Path.Combine(cacheArea, "recovery"));
        var firstHash = (await cached.ScanAsync(default)).Entries.Single(x => x.Path == "same-size.txt").ContentHash;
        var stamp = new FileInfo(file).LastWriteTimeUtc;
        await File.WriteAllTextAsync(file, "bbbb");
        File.SetLastWriteTimeUtc(file, stamp);
        var stale = (await cached.ScanAsync(default)).Entries.Single(x => x.Path == "same-size.txt");
        Check(stale.ContentHash == firstHash, "fingerprint cache did not skip re-hashing an unchanged length and timestamp");
        // The cache is advisory: acting on the stale entry must still be refused by the endpoint.
        await Reject(() => cached.DeleteAsync(stale, default), "a stale cached hash allowed a delete");
        await File.WriteAllTextAsync(file, "cccccccc");
        var refreshed = (await cached.ScanAsync(default)).Entries.Single(x => x.Path == "same-size.txt");
        Check(refreshed.ContentHash != firstHash, "cache was not invalidated by a real change");
        Console.WriteLine("PASS: local fingerprint cache reused, invalidated by real changes and never trusted for writes");

        var server = new TagServer();
        using var provider = new WebDavProvider(() => server);
        await provider.ConnectAsync(new Dictionary<string, string>
        { ["url"] = "https://webdav.test/root/", ["username"] = "user", ["password"] = "secret" }, default);
        var endpoint = provider.OpenEndpoint("https://webdav.test/root/");
        var initial = await endpoint.ScanAsync(default);
        Check(initial.IsComplete && server.Gets == 1, "first remote scan did not read the file exactly once");
        var repeated = await endpoint.ScanAsync(default);
        Check(server.Gets == 1, "unchanged strong ETag still triggered a download");
        Check(repeated.Entries.Single(x => x.Path == "a.txt").ContentHash == initial.Entries.Single(x => x.Path == "a.txt").ContentHash, "cached remote hash changed");
        server.Data = Encoding.UTF8.GetBytes("second"); server.Tag = "\"v2\"";
        var changed = await endpoint.ScanAsync(default);
        Check(server.Gets == 2 && changed.Entries.Single(x => x.Path == "a.txt").ContentHash != initial.Entries.Single(x => x.Path == "a.txt").ContentHash, "changed ETag did not force a re-read");
        server.Tag = "W/\"v3\"";
        await endpoint.ScanAsync(default); await endpoint.ScanAsync(default);
        Check(server.Gets == 4, "weak ETags must never be cached");
        Console.WriteLine("PASS: remote hashes reused only for unchanged strong ETags, never for weak ones");
    }
}

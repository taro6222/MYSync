using System.Security.Cryptography;
using System.Text;
using MYSync.Sync.Core;
using MYSync.Sync.Infrastructure;

internal sealed class MemoryEndpoint : ISyncEndpoint
{
    private readonly Dictionary<string, byte[]?> files = new(StringComparer.Ordinal);
    public bool FailAfterPut { get; set; }
    public int Writes { get; private set; }
    public void Seed(string path, string text) => files[path] = Encoding.UTF8.GetBytes(text);
    public void Folder(string path) => files[path] = null;
    public void Remove(string path) => files.Remove(path);
    public string Text(string path) => Encoding.UTF8.GetString(files[path]!);
    private SyncEntry? Entry(string path) => !files.TryGetValue(path, out var data) ? null : new(path, data is null ? EntryKind.Directory : EntryKind.File, data is null ? null : Convert.ToHexString(SHA256.HashData(data)));
    public Task<ScanResult> ScanAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(new ScanResult(files.Keys.Select(x => Entry(x)!).ToArray(), [])); }
    public Task<Stream> OpenReadAsync(SyncEntry expected, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Entry(expected.Path) != expected) throw new SyncPreconditionException("source changed");
        return Task.FromResult<Stream>(new MemoryStream(files[expected.Path]!.ToArray(), false));
    }
    public async Task PutFileAsync(string path, SyncEntry? expected, Stream content, string hash, CancellationToken ct)
    {
        using var buffer = new MemoryStream(); await content.CopyToAsync(buffer, ct); var bytes = buffer.ToArray();
        if (Convert.ToHexString(SHA256.HashData(bytes)) != hash) throw new SyncPreconditionException("hash mismatch");
        if (Entry(path) != expected) throw new SyncPreconditionException("target changed");
        ct.ThrowIfCancellationRequested(); files[path] = bytes; Writes++;
        if (FailAfterPut) { FailAfterPut = false; throw new IOException("simulated disconnect after publish"); }
    }
    public Task CreateDirectoryAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); if (files.ContainsKey(path)) throw new SyncPreconditionException("already exists"); files[path] = null; return Task.CompletedTask;
    }
    public Task DeleteAsync(SyncEntry expected, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Entry(expected.Path) != expected || files.Keys.Any(x => x.StartsWith(expected.Path + "/", StringComparison.Ordinal))) throw new SyncPreconditionException("changed or nonempty");
        files.Remove(expected.Path); return Task.CompletedTask;
    }
}
internal static class ExecutionChecks
{
    public static async Task Run(string scratch)
    {
        static void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); }
        var journal = new SyncJournal(Path.Combine(scratch, "execution.db"));
        async Task Queue(Guid id, MemoryEndpoint l, MemoryEndpoint r) => journal.Enqueue(id, SyncPlanner.Compare(await l.ScanAsync(default), await r.ScanAsync(default), journal.ReadBaseline(id)));
        var local = new MemoryEndpoint(); var remote = new MemoryEndpoint(); var pair = Guid.NewGuid();
        local.Folder("dir"); local.Seed("dir/local", "upload"); remote.Seed("remote", "download");
        await Queue(pair, local, remote);
        var executor = new SyncExecutor(journal);
        Check((await executor.RunAsync(pair, local, remote)).Converged, "initial merge failed");
        Check(local.Text("remote") == "download" && remote.Text("dir/local") == "upload", "roundtrip content");
        local.Seed("dir/local", "updated"); await Queue(pair, local, remote);
        Check((await executor.RunAsync(pair, local, remote)).Converged && remote.Text("dir/local") == "updated", "update failed");
        local.Remove("dir/local"); local.Remove("dir"); await Queue(pair, local, remote);
        Check((await executor.RunAsync(pair, local, remote)).Converged, "child-first delete failed");
        Check(!(await remote.ScanAsync(default)).Entries.Any(x => x.Path.StartsWith("dir")), "directory remains");
        Console.WriteLine("PASS: executor bidirectional merge, update, child-first directory deletion, verified baseline");

        var crashPair = Guid.NewGuid(); var a = new MemoryEndpoint(); var b = new MemoryEndpoint { FailAfterPut = true }; a.Seed("a", "one");
        await Queue(crashPair, a, b);
        Check(!(await executor.RunAsync(crashPair, a, b)).Converged, "uncertain write accepted");
        var reopened = new SyncJournal(Path.Combine(scratch, "execution.db")); reopened.RecoverInterrupted();
        Check((await new SyncExecutor(reopened).RunAsync(crashPair, a, b)).Converged && b.Writes == 1, "uncertain write duplicated");

        var stalePair = Guid.NewGuid(); var x = new MemoryEndpoint(); var y = new MemoryEndpoint(); x.Seed("a", "planned");
        await Queue(stalePair, x, y); y.Seed("a", "external edit");
        Check(!(await executor.RunAsync(stalePair, x, y)).Converged && y.Text("a") == "external edit", "stale plan overwrote target");
        Console.WriteLine("PASS: uncertain publish recovery without repeat write; changed destination preserved");

        var conflictPair = Guid.NewGuid(); var cl = new MemoryEndpoint(); var cr = new MemoryEndpoint(); cl.Seed("file", "left"); cr.Seed("file", "right");
        await Queue(conflictPair, cl, cr);
        Check(!(await executor.RunAsync(conflictPair, cl, cr)).Converged, "conflict silently resolved");
        Check(cl.Text("file") == "left" && cr.Text("file") == "right", "conflict original changed");
        var copies = (await cl.ScanAsync(default)).Entries.Where(e => e.Path.Contains(".conflict-")).ToArray();
        Check(copies.Length == 2 && copies.All(e => cl.Text(e.Path) == cr.Text(e.Path)), "conflict copies missing");
        var count = cl.Writes + cr.Writes; await executor.RunAsync(conflictPair, cl, cr);
        Check(cl.Writes + cr.Writes == count, "retry duplicates conflict copies");
        Console.WriteLine("PASS: both conflict versions preserved on both sides; originals retained; retry idempotent");
    }
}

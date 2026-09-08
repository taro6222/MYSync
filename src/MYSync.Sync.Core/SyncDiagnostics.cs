using System.Text.Json;

namespace MYSync.Sync.Core;

/// <summary>Structured diagnostics without URLs, headers, bodies or exception messages.</summary>
public static class SyncDiagnostics
{
    private static readonly object Gate = new();
    private static readonly AsyncLocal<(Guid? Pair, long? Job)> Context = new();
    public static string DirectoryPath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MYSync", "logs");
    public static long MaxBytes { get; set; } = 10 * 1024 * 1024;
    public static IDisposable BeginJob(Guid pair, long job)
    {
        var previous = Context.Value;
        Context.Value = (pair, job);
        return new Scope(() => Context.Value = previous);
    }
    public static void Write(string eventName, string? method = null, int? status = null, bool? hasStrongETag = null,
        SyncFailureKind? failure = null, string? exceptionType = null, string? phase = null, long? bytes = null)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var entry = JsonSerializer.Serialize(new { time = now, eventName, pairId = Context.Value.Pair,
                jobId = Context.Value.Job, method, status, hasStrongETag, failure = failure?.ToString(), exceptionType, phase, bytes });
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                var path = Path.Combine(DirectoryPath, "sync.jsonl");
                var payload = System.Text.Encoding.UTF8.GetBytes(entry + "\n");
                var limit = Math.Max(1024, MaxBytes);
                if (payload.Length > limit) return;
                if (File.Exists(path) && new FileInfo(path).Length + payload.Length > limit)
                {
                    var temp = path + ".tmp";
                    try
                    {
                        using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var target = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            var start = Math.Max(0, source.Length - (limit - payload.Length));
                            source.Position = start;
                            if (start > 0) { int c; while ((c = source.ReadByte()) >= 0 && c != 10) { } }
                            source.CopyTo(target); target.Write(payload); target.Flush(true);
                        }
                        File.Move(temp, path, true);
                    }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                }
                else
                {
                    using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                    file.Write(payload);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { /* A diagnostic failure must not change the outcome of a file operation. */ }
    }
    private sealed class Scope(Action restore) : IDisposable
    {
        private bool disposed;
        public void Dispose() { if (!disposed) { disposed = true; restore(); } }
    }
}

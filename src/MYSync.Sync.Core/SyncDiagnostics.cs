using System.Text.Json;

namespace MYSync.Sync.Core;

/// <summary>Structured diagnostics without URLs, headers, bodies or exception messages.</summary>
public static class SyncDiagnostics
{
    private static readonly object Gate = new();
    private static readonly AsyncLocal<(Guid? Pair, long? Job)> Context = new();
    public static string DirectoryPath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MYSync", "logs");
    public static IDisposable BeginJob(Guid pair, long job)
    {
        var previous = Context.Value;
        Context.Value = (pair, job);
        return new Scope(() => Context.Value = previous);
    }
    public static void Write(string eventName, string? method = null, int? status = null, bool? hasStrongETag = null,
        SyncFailureKind? failure = null, string? exceptionType = null)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var entry = JsonSerializer.Serialize(new { time = now, eventName, pairId = Context.Value.Pair,
                jobId = Context.Value.Job, method, status, hasStrongETag, failure = failure?.ToString(), exceptionType });
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                var path = Path.Combine(DirectoryPath, "sync-" + now.ToString("yyyy-MM-dd") + ".jsonl");
                // Bound disk use per day; previous days are retained for diagnosis.
                if (File.Exists(path) && new FileInfo(path).Length >= 10 * 1024 * 1024) return;
                File.AppendAllText(path, entry + Environment.NewLine);
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

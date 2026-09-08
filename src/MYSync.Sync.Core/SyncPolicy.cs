using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;

namespace MYSync.Sync.Core;

public sealed class SyncExclusions
{
    private readonly List<(Regex Pattern, bool Folder, bool Rooted)> rules = [];
    public SyncExclusions(string text = "")
    {
        foreach (var raw in text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var value = raw.Replace('\\', '/');
            if (value.Length > 256 || rules.Count >= 200 || value.StartsWith('/') || value.Contains(':') ||
                value.Any(char.IsControl) || value.Split('/').Any(x => x is "." or ".."))
                throw new ArgumentException("제외 규칙은 256자 이하의 상대 경로로 입력하세요 (최대 200개).");
            var folder = value.EndsWith('/'); value = value.TrimEnd('/');
            if (value.Length == 0) throw new ArgumentException("빈 제외 규칙입니다.");
            var expression = Regex.Escape(value).Replace(@"\*\*/", "(?:.*/)?").Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]");
            rules.Add((new Regex("^" + expression + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100)), folder, value.Contains('/')));
        }
    }
    public bool Matches(string path, EntryKind kind)
    {
        var parts = path.Replace('\\', '/').Split('/');
        for (var i = 0; i < parts.Length; i++)
        {
            var directory = i < parts.Length - 1 || kind == EntryKind.Directory;
            foreach (var rule in rules)
                if ((!rule.Folder || directory) && rule.Pattern.IsMatch(rule.Rooted ? string.Join('/', parts.Take(i + 1)) : parts[i])) return true;
        }
        return false;
    }
}

public sealed class SyncPolicy(string exclusions = "", long speedLimitKiB = 0)
{
    public SyncExclusions Exclusions { get; } = new(exclusions);
    public BandwidthLimiter Bandwidth { get; } = new(speedLimitKiB);
}
public interface ISyncPolicyEndpoint
{
    SyncPolicy Policy { get; set; }
}

/// <summary>Shared upload/download payload budget per connection; idle time cannot accumulate a burst.</summary>
public sealed class BandwidthLimiter
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private double availableAt;
    public long BytesPerSecond { get; }
    public BandwidthLimiter(long kib)
    {
        if (kib < 0 || kib > 1000000) throw new ArgumentOutOfRangeException(nameof(kib));
        BytesPerSecond = kib * 1024;
    }
    public async Task WaitAsync(int bytes, CancellationToken ct)
    {
        if (BytesPerSecond == 0 || bytes == 0) { ct.ThrowIfCancellationRequested(); return; }
        await gate.WaitAsync(ct);
        try
        {
            var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            var target = Math.Max(now, availableAt) + bytes / (double)BytesPerSecond;
            try { await Task.Delay(TimeSpan.FromSeconds(target - now), ct); availableAt = target; }
            catch { availableAt = 0; throw; }
        }
        finally { gate.Release(); }
    }
    public async Task CopyAsync(Stream source, Stream destination, CancellationToken ct)
    {
        var buffer = new byte[16384]; int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        { await WaitAsync(read, ct); await destination.WriteAsync(buffer.AsMemory(0, read), ct); }
    }
    public TimeSpan TimeoutFor(long length) => TimeSpan.FromSeconds(300 + (BytesPerSecond == 0 ? 0 : Math.Max(0, length) / (double)BytesPerSecond * 2));
    public HttpContent Limit(HttpContent content) => BytesPerSecond == 0 ? content : new LimitedContent(content, this);
    private sealed class LimitedContent : HttpContent
    {
        private readonly HttpContent inner;
        private readonly BandwidthLimiter limiter;
        public LimitedContent(HttpContent inner, BandwidthLimiter limiter)
        {
            this.inner = inner; this.limiter = limiter;
            foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        protected override bool TryComputeLength(out long length) { length = inner.Headers.ContentLength ?? -1; return length >= 0; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        {
            var input = await inner.ReadAsStreamAsync(ct);
            await limiter.CopyAsync(input, stream, ct);
        }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}

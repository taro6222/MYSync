using MYSync.Sync.Core;

namespace MYSync.Sync.Infrastructure;

/// <summary>Defense for saved jobs and conflict actions as well as freshly planned work.</summary>
public sealed class PolicyEndpoint : ISyncEndpoint
{
    private readonly ISyncEndpoint inner;
    private readonly SyncPolicy policy;
    public PolicyEndpoint(ISyncEndpoint inner, SyncPolicy policy)
    {
        this.inner = inner; this.policy = policy;
        if (inner is ISyncPolicyEndpoint configurable) configurable.Policy = policy;
        else if (policy.Bandwidth.BytesPerSecond != 0) throw new InvalidOperationException("이 Provider는 속도 제한을 지원하지 않습니다.");
    }
    public async Task<ScanResult> ScanAsync(CancellationToken ct)
    {
        var result = await inner.ScanAsync(ct);
        var skipped = result.Entries.Where(x => policy.Exclusions.Matches(x.Path, x.Kind)).ToArray();
        return new(result.Entries.Except(skipped).ToArray(), result.Errors,
            result.Unsupported.Concat(skipped.Select(x => new UnsupportedItem(x.Path, "연결별 제외 규칙"))).ToArray());
    }
    private void Check(string path, EntryKind kind)
    { if (policy.Exclusions.Matches(path, kind)) throw new SyncPreconditionException("제외 규칙으로 보호된 항목입니다: " + path); }
    public Task<Stream> OpenReadAsync(SyncEntry expected, CancellationToken ct) { Check(expected.Path, expected.Kind); return inner.OpenReadAsync(expected, ct); }
    public Task PutFileAsync(string path, SyncEntry? expected, Stream content, string sha256, CancellationToken ct)
    { Check(path, EntryKind.File); return inner.PutFileAsync(path, expected, content, sha256, ct); }
    public Task CreateDirectoryAsync(string path, CancellationToken ct) { Check(path, EntryKind.Directory); return inner.CreateDirectoryAsync(path, ct); }
    public Task DeleteAsync(SyncEntry expected, CancellationToken ct) { Check(expected.Path, expected.Kind); return inner.DeleteAsync(expected, ct); }
}

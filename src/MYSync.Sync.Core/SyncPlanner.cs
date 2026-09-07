namespace MYSync.Sync.Core;

public enum EntryKind { File, Directory }
public sealed record SyncEntry(string Path, EntryKind Kind, string? ContentHash);
public sealed record ScanResult(IReadOnlyList<SyncEntry> Entries, IReadOnlyList<string> Errors)
{
    public bool IsComplete => Errors.Count == 0;
}
public enum SyncAction { Upload, Download, DeleteLocal, DeleteRemote, Conflict }
public sealed record PlannedOperation(string Path, SyncAction Action, SyncEntry? ExpectedLocal, SyncEntry? ExpectedRemote, string Reason);
public sealed record SyncPlan(IReadOnlyList<PlannedOperation> Operations, IReadOnlyList<string> Errors)
{
    public bool CanExecute => Errors.Count == 0;
}
public static class SyncPlanner
{
    public static SyncPlan Compare(ScanResult local, ScanResult remote, IReadOnlyList<SyncEntry> baseline)
    {
        var errors = local.Errors.Concat(remote.Errors).ToList();
        var l = Index(local.Entries, errors); var r = Index(remote.Entries, errors); var b = Index(baseline, errors);
        if (errors.Count > 0) return new([], errors);
        var result = new List<PlannedOperation>();
        foreach (var path in l.Keys.Concat(r.Keys).Concat(b.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal))
        {
            l.TryGetValue(path, out var left); r.TryGetValue(path, out var right); b.TryGetValue(path, out var previous);
            if (left is not null && right is not null && left.Path != right.Path)
            { Add(SyncAction.Conflict, "대소문자 경로 충돌"); continue; }
            if (Equivalent(left, right)) continue;
            if (left is not null && right is not null && left.Kind != right.Kind)
            { Add(SyncAction.Conflict, "파일·폴더 유형 충돌"); continue; }
            if (previous is null)
            {
                if (left is null) Add(SyncAction.Download, "원격에 추가됨");
                else if (right is null) Add(SyncAction.Upload, "로컬에 추가됨");
                else Add(SyncAction.Conflict, "최초 내용 충돌 또는 비교 불가");
            }
            else if (Equivalent(left, previous)) Add(right is null ? SyncAction.DeleteLocal : SyncAction.Download, "원격에서 변경됨");
            else if (Equivalent(right, previous)) Add(left is null ? SyncAction.DeleteRemote : SyncAction.Upload, "로컬에서 변경됨");
            else Add(SyncAction.Conflict, "동시 변경 또는 수정·삭제 충돌");
            void Add(SyncAction action, string reason) => result.Add(new(path, action, left, right, reason));
        }
        // A directory cannot be deleted when any descendant was added or changed on the surviving side.
        for (var i = 0; i < result.Count; i++)
        {
            var op = result[i];
            if (op.Action is not (SyncAction.DeleteLocal or SyncAction.DeleteRemote)) continue;
            var surviving = op.Action == SyncAction.DeleteLocal ? l : r;
            if (!surviving.TryGetValue(op.Path, out var entry) || entry.Kind != EntryKind.Directory) continue;
            if (surviving.Values.Any(x => IsChild(x.Path, op.Path) && (!b.TryGetValue(x.Path, out var old) || !Equivalent(x, old))))
                result[i] = op with { Action = SyncAction.Conflict, Reason = "삭제 대상 폴더 내부에 변경이 있음" };
        }
        var blockedParents = result.Where(x => x.Action == SyncAction.Conflict &&
            (x.ExpectedLocal?.Kind == EntryKind.Directory || x.ExpectedRemote?.Kind == EntryKind.Directory)).Select(x => x.Path).ToArray();
        result.RemoveAll(x => blockedParents.Any(p => IsChild(x.Path, p)));
        return new(result.OrderBy(x => x.Action is SyncAction.DeleteLocal or SyncAction.DeleteRemote ? 1 : 0)
            .ThenBy(x => x.Action is SyncAction.DeleteLocal or SyncAction.DeleteRemote ? -x.Path.Count(c => c == '/') : x.Path.Count(c => c == '/')).ToArray(), []);
    }
    private static bool IsChild(string path, string parent) => path.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase);
    private static bool Equivalent(SyncEntry? a, SyncEntry? b) => a is null ? b is null : b is not null && a.Kind == b.Kind &&
        (a.Kind == EntryKind.Directory || a.ContentHash is not null && b.ContentHash is not null && a.ContentHash == b.ContentHash);
    private static Dictionary<string, SyncEntry> Index(IEnumerable<SyncEntry> entries, List<string> errors)
    {
        var result = new Dictionary<string, SyncEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Path) || entry.Path.Contains('\\') || entry.Path.Contains(':') || entry.Path.Split('/').Any(x => x is "" or "." or ".."))
            { errors.Add("유효하지 않은 상대 경로: " + entry.Path); continue; }
            if (!result.TryAdd(entry.Path, entry)) errors.Add("중복 경로: " + entry.Path);
        }
        foreach (var entry in result.Values)
        {
            var parent = entry.Path;
            while (parent.Contains('/'))
            {
                parent = parent[..parent.LastIndexOf('/')];
                if (!result.TryGetValue(parent, out var p) || p.Kind != EntryKind.Directory) errors.Add("부모 폴더 누락 또는 유형 오류: " + entry.Path);
            }
        }
        return result;
    }
}

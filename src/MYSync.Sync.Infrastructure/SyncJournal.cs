using Microsoft.Data.Sqlite;
using System.Text.Json;
using MYSync.Sync.Core;
namespace MYSync.Sync.Infrastructure;
public enum JobState { Pending, Running, NeedsReconcile, Completed, Applied }
public sealed record JournalJob(long Id, Guid PairId, PlannedOperation Operation, JobState State,
    string? FailureReason = null, SyncFailureKind? FailureKind = null, string? FailedAt = null);
public sealed record ConflictResolution(long JobId, PlannedOperation Operation);
public sealed class SyncJournal
{
    private readonly string connectionString;
    public SyncJournal(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS Baselines(PairId TEXT PRIMARY KEY, Payload TEXT NOT NULL); CREATE TABLE IF NOT EXISTS Jobs(Id INTEGER PRIMARY KEY AUTOINCREMENT, PairId TEXT NOT NULL, Payload TEXT NOT NULL, State INTEGER NOT NULL);";
        cmd.ExecuteNonQuery();
        foreach (var column in new[] { "FailureReason", "FailureKind", "FailedAt" })
        {
            using var check = c.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Jobs') WHERE name=$name";
            check.Parameters.AddWithValue("$name", column);
            if ((long)check.ExecuteScalar()! == 0)
            {
                using var alter = c.CreateCommand();
                alter.CommandText = $"ALTER TABLE Jobs ADD COLUMN {column} TEXT NULL";
                alter.ExecuteNonQuery();
            }
        }
    }
    private SqliteConnection Open() { var c = new SqliteConnection(connectionString); c.Open(); return c; }
    public IReadOnlyList<SyncEntry> ReadBaseline(Guid pair)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Payload FROM Baselines WHERE PairId=$pair"; cmd.Parameters.AddWithValue("$pair", pair.ToString());
        return cmd.ExecuteScalar() is string value ? JsonSerializer.Deserialize<SyncEntry[]>(value)! : [];
    }
    public void Enqueue(Guid pair, SyncPlan plan)
    {
        if (!plan.CanExecute) throw new InvalidOperationException("불완전한 검사 결과는 큐에 넣을 수 없습니다.");
        using var c = Open(); using var tx = c.BeginTransaction();
        using (var check = c.CreateCommand())
        {
            check.Transaction = tx; check.CommandText = "SELECT COUNT(*) FROM Jobs WHERE PairId=$pair AND State<>$done";
            check.Parameters.AddWithValue("$pair", pair.ToString()); check.Parameters.AddWithValue("$done", (int)JobState.Completed);
            if ((long)check.ExecuteScalar()! != 0) throw new InvalidOperationException("기존 작업을 재검사하거나 완료해야 합니다.");
        }
        foreach (var operation in plan.Operations)
        {
            using var cmd = c.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO Jobs(PairId,Payload,State) VALUES($pair,$payload,$state)";
            cmd.Parameters.AddWithValue("$pair", pair.ToString()); cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(operation));
            cmd.Parameters.AddWithValue("$state", (int)(operation.Action == SyncAction.Conflict ? JobState.NeedsReconcile : JobState.Pending)); cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
    public IReadOnlyList<JournalJob> ReadJobs(Guid pair)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id,Payload,State,FailureReason,FailureKind,FailedAt FROM Jobs WHERE PairId=$pair ORDER BY Id"; cmd.Parameters.AddWithValue("$pair", pair.ToString());
        using var reader = cmd.ExecuteReader(); var jobs = new List<JournalJob>();
        while (reader.Read()) jobs.Add(new(reader.GetInt64(0), pair, JsonSerializer.Deserialize<PlannedOperation>(reader.GetString(1))!, (JobState)reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            !reader.IsDBNull(4) && Enum.TryParse<SyncFailureKind>(reader.GetString(4), out var kind) ? kind : null,
            reader.IsDBNull(5) ? null : reader.GetString(5)));
        return jobs;
    }
    public void SkipExcluded(Guid pair, SyncExclusions exclusions)
    {
        foreach (var job in ReadJobs(pair).Where(x => x.State != JobState.Completed))
        {
            if (job.State == JobState.Running) throw new InvalidOperationException("실행 중인 작업에는 제외 규칙을 변경할 수 없습니다.");
            if (exclusions.Matches(job.Operation.Path, (job.Operation.ExpectedLocal ?? job.Operation.ExpectedRemote)?.Kind ?? EntryKind.File))
            {
                using var c = Open(); using var cmd = c.CreateCommand();
                cmd.CommandText = "UPDATE Jobs SET State=$done,FailureReason=$reason,FailureKind=NULL WHERE Id=$id AND PairId=$pair AND State<>$running";
                cmd.Parameters.AddWithValue("$done", (int)JobState.Completed); cmd.Parameters.AddWithValue("$reason", "사용자 제외 규칙으로 실행하지 않음");
                cmd.Parameters.AddWithValue("$id", job.Id); cmd.Parameters.AddWithValue("$pair", pair.ToString()); cmd.Parameters.AddWithValue("$running", (int)JobState.Running);
                if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("제외 처리 중 작업 상태가 변경되었습니다.");
            }
        }
    }
    /// <summary>Advance only paths whose current contents agree. Unresolved/unsupported paths
    /// retain their prior baseline, so an unrelated failed file cannot turn later edits into first-sync conflicts.</summary>
    public void CommitVerifiedPaths(Guid pair, ScanResult local, ScanResult remote)
    {
        var validation = SyncPlanner.Compare(local, remote, []);
        if (!validation.CanExecute) throw new InvalidOperationException("불완전한 검사로 완료 상태를 저장할 수 없습니다.");
        var left = local.Entries.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        var right = remote.Entries.ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        var baseline = ReadBaseline(pair).ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        bool Protected(string path) => validation.Unsupported.Any(x => path.Equals(x.Path, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(x.Path + "/", StringComparison.OrdinalIgnoreCase));
        bool Equal(string path) => !Protected(path) && left.GetValueOrDefault(path) == right.GetValueOrDefault(path);
        // Older releases could leave verified mutations in Applied without a global baseline.
        // Their expected source is the version successfully published, even if a later edit is
        // already visible now. Recover that common version before the next three-way comparison.
        foreach (var job in ReadJobs(pair).Where(x => x.State == JobState.Applied && !Protected(x.Operation.Path)))
        {
            var op = job.Operation;
            if (op.Action is SyncAction.Upload or SyncAction.Download)
            {
                var published = op.Action == SyncAction.Upload ? op.ExpectedLocal : op.ExpectedRemote;
                if (published is not null) baseline[op.Path] = published;
            }
            else if (op.Action is SyncAction.DeleteLocal or SyncAction.DeleteRemote) baseline.Remove(op.Path);
        }
        foreach (var path in baseline.Keys.Concat(left.Keys).Concat(right.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
        {
            if (!Equal(path)) continue;
            if (left.TryGetValue(path, out var entry)) baseline[path] = entry; else baseline.Remove(path);
        }
        // Keep a structurally valid baseline for protected descendants.
        foreach (var path in baseline.Keys.ToArray())
            for (var parent = path; parent.Contains('/');)
            {
                parent = parent[..parent.LastIndexOf('/')];
                baseline.TryAdd(parent, new(parent, EntryKind.Directory, null));
            }
        using var c = Open(); using var tx = c.BeginTransaction();
        using (var write = c.CreateCommand())
        {
            write.Transaction = tx;
            write.CommandText = "INSERT INTO Baselines VALUES($pair,$payload) ON CONFLICT(PairId) DO UPDATE SET Payload=$payload";
            write.Parameters.AddWithValue("$pair", pair.ToString());
            write.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(baseline.Values));
            write.ExecuteNonQuery();
        }
        foreach (var job in ReadJobs(pair).Where(x => x.State == JobState.Applied && Equal(x.Operation.Path)))
        {
            using var done = c.CreateCommand(); done.Transaction = tx;
            done.CommandText = "UPDATE Jobs SET State=$done WHERE Id=$id AND State=$applied";
            done.Parameters.AddWithValue("$done", (int)JobState.Completed); done.Parameters.AddWithValue("$id", job.Id);
            done.Parameters.AddWithValue("$applied", (int)JobState.Applied); done.ExecuteNonQuery();
        }
        tx.Commit();
    }
    /// <summary>Replace stale queue expectations after a complete scan, retaining unchanged job IDs
    /// (including conflict-copy identities) and their failure history. No endpoint mutations here.</summary>
    public void RefreshPlan(Guid pair, SyncPlan plan)
    {
        if (!plan.CanExecute) throw new InvalidOperationException("불완전한 검사 결과는 큐에 넣을 수 없습니다.");
        var old = ReadJobs(pair).Where(x => x.State != JobState.Completed).ToArray();
        if (old.Any(x => x.State == JobState.Running)) throw new InvalidOperationException("실행 중에는 계획을 교체할 수 없습니다.");
        var remaining = plan.Operations.ToList();
        using var c = Open(); using var tx = c.BeginTransaction();
        foreach (var job in old)
        {
            var match = remaining.FindIndex(op => op.Path == job.Operation.Path && op.Action == job.Operation.Action &&
                op.ExpectedLocal == job.Operation.ExpectedLocal && op.ExpectedRemote == job.Operation.ExpectedRemote);
            if (match >= 0 && job.State != JobState.Applied) { remaining.RemoveAt(match); continue; }
            using var done = c.CreateCommand(); done.Transaction = tx;
            done.CommandText = "UPDATE Jobs SET State=$done WHERE Id=$id";
            done.Parameters.AddWithValue("$done", (int)JobState.Completed); done.Parameters.AddWithValue("$id", job.Id); done.ExecuteNonQuery();
        }
        foreach (var op in remaining)
        {
            using var add = c.CreateCommand(); add.Transaction = tx;
            add.CommandText = "INSERT INTO Jobs(PairId,Payload,State) VALUES($pair,$payload,$state)";
            add.Parameters.AddWithValue("$pair", pair.ToString()); add.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(op));
            add.Parameters.AddWithValue("$state", (int)(op.Action == SyncAction.Conflict ? JobState.NeedsReconcile : JobState.Pending));
            add.ExecuteNonQuery();
        }
        tx.Commit();
    }
    public bool TryStart(long id)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE Jobs SET State=$running WHERE Id=$id AND State=$pending";
        cmd.Parameters.AddWithValue("$running", (int)JobState.Running); cmd.Parameters.AddWithValue("$pending", (int)JobState.Pending); cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() == 1;
    }
    public bool TryAcquire(long id)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE Jobs SET State=$running WHERE Id=$id AND State IN ($pending,$reconcile)";
        cmd.Parameters.AddWithValue("$running", (int)JobState.Running); cmd.Parameters.AddWithValue("$pending", (int)JobState.Pending);
        cmd.Parameters.AddWithValue("$reconcile", (int)JobState.NeedsReconcile); cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() == 1;
    }
    public void SetOutcome(long id, JobState state, string? failureReason = null, SyncFailureKind? failureKind = null)
    {
        if (state is not (JobState.Applied or JobState.NeedsReconcile)) throw new ArgumentOutOfRangeException(nameof(state));
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE Jobs SET State=$state, FailureReason=$reason, FailureKind=$kind, FailedAt=$at WHERE Id=$id AND State=$running";
        cmd.Parameters.AddWithValue("$reason", (object?)failureReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$kind", (object?)failureKind?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", failureReason is null ? DBNull.Value : DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$state", (int)state); cmd.Parameters.AddWithValue("$running", (int)JobState.Running); cmd.Parameters.AddWithValue("$id", id);
        if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("실행 중인 작업만 결과를 기록할 수 있습니다.");
    }
    // Call only at startup, before any workers start. Never blindly replay an interrupted side effect.
    public void RecoverInterrupted()
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE Jobs SET State=$reconcile WHERE State=$running";
        cmd.Parameters.AddWithValue("$reconcile", (int)JobState.NeedsReconcile); cmd.Parameters.AddWithValue("$running", (int)JobState.Running); cmd.ExecuteNonQuery();
    }
    // Commit a new baseline only after a complete re-scan proves both sides have converged.
    public void CommitConverged(Guid pair, ScanResult local, ScanResult remote)
    {
        var verification = SyncPlanner.Compare(local, remote, []);
        if (!verification.CanExecute || verification.Operations.Count != 0) throw new InvalidOperationException("양쪽 상태가 일치하지 않습니다.");
        using var c = Open(); using var tx = c.BeginTransaction(); using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO Baselines VALUES($pair,$payload) ON CONFLICT(PairId) DO UPDATE SET Payload=$payload; UPDATE Jobs SET State=$done WHERE PairId=$pair;";
        cmd.Parameters.AddWithValue("$pair", pair.ToString()); cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(local.Entries)); cmd.Parameters.AddWithValue("$done", (int)JobState.Completed);
        cmd.ExecuteNonQuery(); tx.Commit();
    }

    private static IReadOnlyList<SyncEntry> ReadBaseline(SqliteConnection c, SqliteTransaction tx, Guid pair)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT Payload FROM Baselines WHERE PairId=$pair"; cmd.Parameters.AddWithValue("$pair", pair.ToString());
        return cmd.ExecuteScalar() is string value ? JsonSerializer.Deserialize<SyncEntry[]>(value)! : [];
    }
    /// <summary>
    /// Records an explicit user decision for one unresolved file conflict by moving the discarded side into the baseline,
    /// so the existing three-way comparison produces the intended transfer. Refuses directory/type conflicts, stale state,
    /// and any resolution that would change the planned work for another path. Both conflict copies stay on disk.
    /// </summary>
    public ConflictResolution ResolveConflict(Guid pair, long jobId, ScanResult local, ScanResult remote, bool keepLocal)
    {
        if (!local.IsComplete || !remote.IsComplete) throw new InvalidOperationException("불완전한 검사 결과로는 충돌을 해결할 수 없습니다.");
        using var c = Open(); using var tx = c.BeginTransaction();
        JournalJob job;
        using (var read = c.CreateCommand())
        {
            read.Transaction = tx; read.CommandText = "SELECT Id,Payload,State FROM Jobs WHERE Id=$id AND PairId=$pair";
            read.Parameters.AddWithValue("$id", jobId); read.Parameters.AddWithValue("$pair", pair.ToString());
            using var reader = read.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException("충돌 작업을 찾을 수 없습니다.");
            job = new(reader.GetInt64(0), pair, JsonSerializer.Deserialize<PlannedOperation>(reader.GetString(1))!, (JobState)reader.GetInt32(2));
        }
        var op = job.Operation;
        if (job.State != JobState.NeedsReconcile || op.Action != SyncAction.Conflict) throw new InvalidOperationException("미해결 충돌만 해결할 수 있습니다.");
        if (op.ExpectedLocal?.Kind == EntryKind.Directory || op.ExpectedRemote?.Kind == EntryKind.Directory) throw new InvalidOperationException("폴더·유형 충돌은 이 화면에서 해결하지 않습니다.");
        var currentLocal = local.Entries.SingleOrDefault(x => string.Equals(x.Path, op.Path, StringComparison.OrdinalIgnoreCase));
        var currentRemote = remote.Entries.SingleOrDefault(x => string.Equals(x.Path, op.Path, StringComparison.OrdinalIgnoreCase));
        if (currentLocal != op.ExpectedLocal || currentRemote != op.ExpectedRemote) throw new InvalidOperationException("충돌 항목이 검사 이후 변경되었습니다. 다시 검사하세요.");
        if (keepLocal ? currentLocal is null : currentRemote is null) throw new InvalidOperationException("보존하도록 선택한 항목이 없습니다.");
        var previous = ReadBaseline(c, tx, pair);
        var baseline = previous.Where(x => !string.Equals(x.Path, op.Path, StringComparison.OrdinalIgnoreCase)).ToList();
        var discarded = keepLocal ? currentRemote : currentLocal;
        if (discarded is not null)
        {
            baseline.Add(discarded);
            for (var parent = op.Path; parent.Contains('/');)
            {
                parent = parent[..parent.LastIndexOf('/')];
                if (!baseline.Any(x => string.Equals(x.Path, parent, StringComparison.OrdinalIgnoreCase))) baseline.Add(new(parent, EntryKind.Directory, null));
            }
        }
        var after = SyncPlanner.Compare(local, remote, baseline);
        if (!after.CanExecute) throw new InvalidOperationException("해결 후 계획을 만들 수 없습니다: " + string.Join(" / ", after.Errors));
        if (!Signature(SyncPlanner.Compare(local, remote, previous), op.Path).SequenceEqual(Signature(after, op.Path), StringComparer.Ordinal))
            throw new InvalidOperationException("이 해결이 다른 경로의 작업까지 바꿉니다. 중단합니다.");
        var resolved = after.Operations.SingleOrDefault(x => string.Equals(x.Path, op.Path, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("해결 후 실행할 작업이 없습니다.");
        var expected = keepLocal ? resolved.Action is SyncAction.Upload or SyncAction.DeleteRemote : resolved.Action is SyncAction.Download or SyncAction.DeleteLocal;
        if (!expected) throw new InvalidOperationException("선택한 방향과 다른 작업이 계산되었습니다: " + resolved.Action);
        using (var write = c.CreateCommand())
        {
            write.Transaction = tx;
            write.CommandText = "INSERT INTO Baselines VALUES($pair,$payload) ON CONFLICT(PairId) DO UPDATE SET Payload=$payload;" +
                " UPDATE Jobs SET State=$done WHERE Id=$id AND State=$reconcile;" +
                " INSERT INTO Jobs(PairId,Payload,State) VALUES($pair,$job,$pending);";
            write.Parameters.AddWithValue("$pair", pair.ToString()); write.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(baseline));
            write.Parameters.AddWithValue("$done", (int)JobState.Completed); write.Parameters.AddWithValue("$id", jobId);
            write.Parameters.AddWithValue("$reconcile", (int)JobState.NeedsReconcile);
            write.Parameters.AddWithValue("$job", JsonSerializer.Serialize(resolved)); write.Parameters.AddWithValue("$pending", (int)JobState.Pending);
            write.ExecuteNonQuery();
        }
        tx.Commit();
        return new(jobId, resolved);
    }
    private static string[] Signature(SyncPlan plan, string path) => plan.Operations
        .Where(x => !string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase))
        .Select(x => x.Path + "|" + x.Action).Order(StringComparer.Ordinal).ToArray();
}

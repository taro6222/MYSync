using Microsoft.Data.Sqlite;
using System.Text.Json;
using MYSync.Sync.Core;
namespace MYSync.Sync.Infrastructure;
public enum JobState { Pending, Running, NeedsReconcile, Completed, Applied }
public sealed record JournalJob(long Id, Guid PairId, PlannedOperation Operation, JobState State);
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
        cmd.CommandText = "SELECT Id,Payload,State FROM Jobs WHERE PairId=$pair ORDER BY Id"; cmd.Parameters.AddWithValue("$pair", pair.ToString());
        using var reader = cmd.ExecuteReader(); var jobs = new List<JournalJob>();
        while (reader.Read()) jobs.Add(new(reader.GetInt64(0), pair, JsonSerializer.Deserialize<PlannedOperation>(reader.GetString(1))!, (JobState)reader.GetInt32(2)));
        return jobs;
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
    public void SetOutcome(long id, JobState state)
    {
        if (state is not (JobState.Applied or JobState.NeedsReconcile)) throw new ArgumentOutOfRangeException(nameof(state));
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE Jobs SET State=$state WHERE Id=$id AND State=$running";
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
}

using Microsoft.Data.Sqlite;
using MYSync.Sync.Core;
using MYSync.Sync.Infrastructure;
using MYSync.Provider.WebDav;
using System.Net;
using System.Text.Json;

internal static class DiagnosticChecks
{
    private sealed class Refused : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
    }
    public static async Task Run(string scratch)
    {
        static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        var path = Path.Combine(scratch, "legacy-journal.db");
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
        {
            c.Open(); using var command = c.CreateCommand();
            command.CommandText = "CREATE TABLE Jobs(Id INTEGER PRIMARY KEY AUTOINCREMENT,PairId TEXT NOT NULL,Payload TEXT NOT NULL,State INTEGER NOT NULL)";
            command.ExecuteNonQuery();
        }
        var pair = Guid.NewGuid(); var entry = new SyncEntry("file.txt", EntryKind.File, "hash");
        var journal = new SyncJournal(path);
        journal.Enqueue(pair, SyncPlanner.Compare(new([entry], []), new([], []), []));
        var job = journal.ReadJobs(pair).Single();
        Check(journal.TryAcquire(job.Id), "could not acquire legacy job");
        journal.SetOutcome(job.Id, JobState.NeedsReconcile, "강한 ETag 없음", SyncFailureKind.Precondition);
        var reopened = new SyncJournal(path);
        var failed = reopened.ReadJobs(pair).Single();
        Check(failed.FailureReason == "강한 ETag 없음" && failed.FailureKind == SyncFailureKind.Precondition && failed.FailedAt is not null,
            "failure diagnostics lost after reopening migrated journal");
        Check(reopened.TryAcquire(job.Id), "failed job not resumable");
        reopened.SetOutcome(job.Id, JobState.Applied);
        Check(reopened.ReadJobs(pair).Single().FailureReason is null, "success retained stale error");

        using (SyncDiagnostics.BeginJob(pair, job.Id))
        {
            using var provider = new WebDavProvider(() => new Refused());
            try { await provider.ConnectAsync(new Dictionary<string, string>
                { ["url"] = "https://private-host.test/private-folder/", ["username"] = "private-user", ["password"] = "private-secret" }, default); }
            catch (WebDavException) { }
        }
        var lines = Directory.GetFiles(SyncDiagnostics.DirectoryPath, "*.jsonl").SelectMany(File.ReadAllLines).ToArray();
        Check(!string.Join('\n', lines).Contains("private-"), "HTTP diagnostics leaked connection fields");
        Check(lines.Any(line =>
        {
            using var json = JsonDocument.Parse(line); var root = json.RootElement;
            return root.GetProperty("pairId").ToString() == pair.ToString() && root.GetProperty("method").ToString() == "PROPFIND"
                && root.GetProperty("status").GetInt32() == 401;
        }), "HTTP response did not retain job correlation and status");
        var directory = SyncDiagnostics.DirectoryPath;
        var blocked = Path.Combine(scratch, "not-a-log-directory"); File.WriteAllText(blocked, "occupied");
        try { SyncDiagnostics.DirectoryPath = blocked; SyncDiagnostics.Write("test.unwritable"); }
        finally { SyncDiagnostics.DirectoryPath = directory; }
        Console.WriteLine("PASS: legacy journal migration, persistent failure reason, successful retry clears error, correlated HTTP logs without credentials, log failure isolated");
    }
}

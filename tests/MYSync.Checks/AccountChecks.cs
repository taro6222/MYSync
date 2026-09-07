using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using MYSync.Sync.Core;
using MYSync.Sync.Infrastructure;

internal static class AccountChecks
{
    public static void Run(string scratch)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI validation requires Windows.");
        static void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); }
        var path = Path.Combine(scratch, "accounts.db");
        var a = new SavedAccount(Guid.NewGuid(), "mysync.webdav", "Test account A");
        var b = new SavedAccount(Guid.NewGuid(), "mysync.webdav", "Test account B");
        var password = "test-secret-" + Guid.NewGuid().ToString("N");
        var values = new Dictionary<string,string> { ["url"] = "https://test.invalid/", ["port"] = "5006", ["username"] = "user", ["password"] = password };
        new AccountStore(path).Save(a, values);
        var reopened = new AccountStore(path);
        Check(reopened.List().Single() == a && reopened.ReadValues(a)["password"] == password && reopened.ReadValues(a)["port"] == "5006", "encrypted account restoration");
        SqliteConnection.ClearAllPools();
        Check(!Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains(password, StringComparison.Ordinal), "plaintext in DB");
        values["password"] = "updated-test-value";
        reopened.Save(a, values); reopened.Save(b, values);
        Check(reopened.List().Count == 2 && reopened.ReadValues(a)["password"] == values["password"], "account update/isolation");
        var settings = new SettingsStore(path);
        var pair = new SyncPair(Guid.NewGuid(), a.ProviderId, Path.Combine(scratch, "local"), "remote", "Remote", true, a.Id);
        settings.Save(pair);
        Check(new SettingsStore(path).Load().Single() == pair, "account link restoration");
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
        {
            c.Open(); using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE Accounts SET ProtectedValues=(SELECT ProtectedValues FROM Accounts WHERE Id=$a) WHERE Id=$b";
            cmd.Parameters.AddWithValue("$a", a.Id.ToString()); cmd.Parameters.AddWithValue("$b", b.Id.ToString()); cmd.ExecuteNonQuery();
        }
        try { reopened.ReadValues(b); throw new Exception("swapped ciphertext accepted"); } catch (CryptographicException) { }
        Check(reopened.ReadValues(a)["password"] == "updated-test-value", "other account damaged");
        Console.WriteLine("PASS: Windows DPAPI account restore, encrypted storage, account identity binding, pair account persistence");

        var legacyPath = Path.Combine(scratch, "legacy.db"); var oldId = Guid.NewGuid();
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = legacyPath }.ToString()))
        {
            c.Open(); using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE SyncPairs(Id TEXT PRIMARY KEY,ProviderId TEXT NOT NULL,LocalPath TEXT NOT NULL,RemoteFolderId TEXT NOT NULL,RemoteFolderName TEXT NOT NULL,Paused INTEGER NOT NULL); INSERT INTO SyncPairs VALUES($id,'mysync.sample','local','remote','Remote',1)";
            cmd.Parameters.AddWithValue("$id", oldId.ToString()); cmd.ExecuteNonQuery();
        }
        var legacy = new SettingsStore(legacyPath).Load().Single();
        Check(legacy.Id == oldId && legacy.AccountId is null, "legacy row lost");
        new SettingsStore(legacyPath).Save(legacy with { AccountId = a.Id });
        Check(new SettingsStore(legacyPath).Load().Single().AccountId == a.Id, "migration idempotence");
        Console.WriteLine("PASS: legacy settings migration preserves data and supports repeat initialization");
    }
}

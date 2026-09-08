using Microsoft.Data.Sqlite;
using MYSync.Sync.Core;
namespace MYSync.Sync.Infrastructure;
public sealed class SettingsStore
{
    private readonly string connectionString;
    public SettingsStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS SyncPairs (Id TEXT PRIMARY KEY, ProviderId TEXT NOT NULL, LocalPath TEXT NOT NULL, RemoteFolderId TEXT NOT NULL, RemoteFolderName TEXT NOT NULL, Paused INTEGER NOT NULL)";
        command.ExecuteNonQuery();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('SyncPairs') WHERE name='AccountId'";
        if ((long)command.ExecuteScalar()! == 0)
        {
            command.CommandText = "ALTER TABLE SyncPairs ADD COLUMN AccountId TEXT NULL";
            command.ExecuteNonQuery();
        }
        MigrateOptions();
    }
    private void MigrateOptions()
    {
        using var c = Open();
        foreach (var (name, definition) in new[] { ("Exclusions", "TEXT NOT NULL DEFAULT ''"), ("SpeedLimitKiB", "INTEGER NOT NULL DEFAULT 0") })
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('SyncPairs') WHERE name=$name";
            cmd.Parameters.AddWithValue("$name", name);
            if ((long)cmd.ExecuteScalar()! == 0) { cmd.CommandText = $"ALTER TABLE SyncPairs ADD COLUMN {name} {definition}"; cmd.ExecuteNonQuery(); }
        }
    }
    private SqliteConnection Open() { var c = new SqliteConnection(connectionString); c.Open(); return c; }
    public void Save(SyncPair pair) => Save(pair, null);
    // One atomic statement replaces the identity so old journals cannot target new roots.
    public void Replace(Guid previousId, SyncPair pair) => Save(pair, previousId);
    private void Save(SyncPair pair, Guid? previousId)
    {
        _ = new SyncExclusions(pair.Exclusions);
        if (pair.SpeedLimitKiB < 0 || pair.SpeedLimitKiB > 1000000) throw new ArgumentOutOfRangeException(nameof(pair));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO SyncPairs(Id,ProviderId,LocalPath,RemoteFolderId,RemoteFolderName,Paused,AccountId,Exclusions,SpeedLimitKiB) VALUES ($id,$provider,$local,$remote,$name,$paused,$account,$exclude,$speed) ON CONFLICT(Id) DO UPDATE SET ProviderId=$provider,LocalPath=$local,RemoteFolderId=$remote,RemoteFolderName=$name,Paused=$paused,AccountId=$account,Exclusions=$exclude,SpeedLimitKiB=$speed";
        if (previousId is not null)
        {
            command.CommandText = "UPDATE SyncPairs SET Id=$id,ProviderId=$provider,LocalPath=$local,RemoteFolderId=$remote,RemoteFolderName=$name,Paused=$paused,AccountId=$account,Exclusions=$exclude,SpeedLimitKiB=$speed WHERE Id=$previous";
            command.Parameters.AddWithValue("$previous", previousId.Value.ToString());
        }
        command.Parameters.AddWithValue("$exclude", pair.Exclusions);
        command.Parameters.AddWithValue("$speed", pair.SpeedLimitKiB);
        command.Parameters.AddWithValue("$id", pair.Id.ToString());
        command.Parameters.AddWithValue("$provider", pair.ProviderId);
        command.Parameters.AddWithValue("$local", pair.LocalPath);
        command.Parameters.AddWithValue("$remote", pair.RemoteFolderId);
        command.Parameters.AddWithValue("$name", pair.RemoteFolderName);
        command.Parameters.AddWithValue("$paused", pair.Paused);
        command.Parameters.AddWithValue("$account", (object?)pair.AccountId?.ToString() ?? DBNull.Value);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("변경할 동기화 연결을 찾을 수 없습니다.");
    }
    public void Delete(Guid id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM SyncPairs WHERE Id=$id";
        command.Parameters.AddWithValue("$id", id.ToString());
        command.ExecuteNonQuery();
    }
    public IReadOnlyList<SyncPair> Load()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,ProviderId,LocalPath,RemoteFolderId,RemoteFolderName,Paused,AccountId,Exclusions,SpeedLimitKiB FROM SyncPairs ORDER BY rowid";
        using var reader = command.ExecuteReader();
        var result = new List<SyncPair>();
        while (reader.Read()) result.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetBoolean(5), reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6)), reader.GetString(7), reader.GetInt64(8)));
        return result;
    }
}

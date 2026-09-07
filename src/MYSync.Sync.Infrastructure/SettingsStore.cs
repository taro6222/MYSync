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
    }
    private SqliteConnection Open() { var c = new SqliteConnection(connectionString); c.Open(); return c; }
    public void Save(SyncPair pair)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO SyncPairs(Id,ProviderId,LocalPath,RemoteFolderId,RemoteFolderName,Paused,AccountId) VALUES ($id,$provider,$local,$remote,$name,$paused,$account) ON CONFLICT(Id) DO UPDATE SET ProviderId=$provider,LocalPath=$local,RemoteFolderId=$remote,RemoteFolderName=$name,Paused=$paused,AccountId=$account";
        command.Parameters.AddWithValue("$id", pair.Id.ToString());
        command.Parameters.AddWithValue("$provider", pair.ProviderId);
        command.Parameters.AddWithValue("$local", pair.LocalPath);
        command.Parameters.AddWithValue("$remote", pair.RemoteFolderId);
        command.Parameters.AddWithValue("$name", pair.RemoteFolderName);
        command.Parameters.AddWithValue("$paused", pair.Paused);
        command.Parameters.AddWithValue("$account", (object?)pair.AccountId?.ToString() ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
    public IReadOnlyList<SyncPair> Load()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,ProviderId,LocalPath,RemoteFolderId,RemoteFolderName,Paused,AccountId FROM SyncPairs ORDER BY rowid";
        using var reader = command.ExecuteReader();
        var result = new List<SyncPair>();
        while (reader.Read()) result.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetBoolean(5), reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6))));
        return result;
    }
}

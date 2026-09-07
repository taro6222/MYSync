using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MYSync.Sync.Infrastructure;

public sealed record SavedAccount(Guid Id, string ProviderId, string DisplayName);

/// <summary>Connection values are encrypted with Windows DPAPI for the current user.</summary>
public sealed class AccountStore
{
    private readonly string connectionString;
    public AccountStore(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows 계정 저장소가 필요합니다.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS Accounts(Id TEXT PRIMARY KEY, ProviderId TEXT NOT NULL, DisplayName TEXT NOT NULL, Version INTEGER NOT NULL, ProtectedValues BLOB NOT NULL)";
        cmd.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var c = new SqliteConnection(connectionString); c.Open(); return c; }
    private static byte[] Entropy(SavedAccount account) => Encoding.UTF8.GetBytes($"MYSync/accounts/v1/{account.ProviderId}/{account.Id:D}");
    public void Save(SavedAccount account, IReadOnlyDictionary<string, string> values)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (string.IsNullOrWhiteSpace(account.ProviderId) || string.IsNullOrWhiteSpace(account.DisplayName)) throw new ArgumentException("계정 이름과 Provider가 필요합니다.");
        var plain = JsonSerializer.SerializeToUtf8Bytes(values);
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(plain, Entropy(account), DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        using var c = Open(); using var tx = c.BeginTransaction(); using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT ProviderId FROM Accounts WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", account.Id.ToString());
        if (cmd.ExecuteScalar() is string existing && existing != account.ProviderId) throw new InvalidOperationException("기존 계정의 Provider는 바꿀 수 없습니다.");
        cmd.CommandText = "INSERT INTO Accounts VALUES($id,$provider,$name,1,$values) ON CONFLICT(Id) DO UPDATE SET DisplayName=$name,Version=1,ProtectedValues=$values";
        cmd.Parameters.AddWithValue("$provider", account.ProviderId); cmd.Parameters.AddWithValue("$name", account.DisplayName); cmd.Parameters.AddWithValue("$values", encrypted);
        cmd.ExecuteNonQuery(); tx.Commit();
    }
    public IReadOnlyList<SavedAccount> List()
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT Id,ProviderId,DisplayName FROM Accounts ORDER BY rowid";
        using var r = cmd.ExecuteReader(); var accounts = new List<SavedAccount>();
        while (r.Read()) accounts.Add(new(Guid.Parse(r.GetString(0)), r.GetString(1), r.GetString(2)));
        return accounts;
    }
    /// <summary>Changes only the display name. Credentials and the account identity are untouched.</summary>
    public void Rename(Guid id, string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("계정 이름을 입력하세요.");
        if (displayName.Length > 120 || displayName.Any(char.IsControl)) throw new ArgumentException("계정 이름을 사용할 수 없습니다.");
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE Accounts SET DisplayName=$name WHERE Id=$id";
        cmd.Parameters.AddWithValue("$name", displayName.Trim()); cmd.Parameters.AddWithValue("$id", id.ToString());
        if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("저장된 계정을 찾을 수 없습니다.");
    }
    /// <summary>Removes the account and its encrypted values. The caller must first ensure no sync pair references it.</summary>
    public void Delete(Guid id)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM Accounts WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("저장된 계정을 찾을 수 없습니다.");
    }
    public Dictionary<string, string> ReadValues(SavedAccount account)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Version,ProtectedValues FROM Accounts WHERE Id=$id AND ProviderId=$provider";
        cmd.Parameters.AddWithValue("$id", account.Id.ToString()); cmd.Parameters.AddWithValue("$provider", account.ProviderId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new InvalidOperationException("저장된 계정을 찾을 수 없습니다.");
        if (r.GetInt32(0) != 1) throw new InvalidOperationException("지원하지 않는 계정 저장 형식입니다.");
        var plain = ProtectedData.Unprotect((byte[])r[1], Entropy(account), DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(plain) ?? throw new InvalidDataException("계정 데이터가 비었습니다."); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}

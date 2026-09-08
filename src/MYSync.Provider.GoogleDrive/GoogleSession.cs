using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Util.Store;
using Newtonsoft.Json;
using MYSync.Sync.Core;

namespace MYSync.Provider.GoogleDrive;

public interface IGoogleSession : IDisposable
{
    Task<string> AccessTokenAsync(CancellationToken ct);
    string ExportToken();
}

internal sealed class GoogleSession(UserCredential credential) : IGoogleSession
{
    public const string Scope = "https://www.googleapis.com/auth/drive";
    public static async Task<IGoogleSession> ConnectAsync(IReadOnlyDictionary<string, string> values, CancellationToken ct)
    {
        var secrets = new ClientSecrets { ClientId = values["client_id"], ClientSecret = values["client_secret"] };
        UserCredential credential;
        if (values.TryGetValue("oauth_token", out var stored))
        {
            if (!values.TryGetValue("oauth_scope", out var savedScope) || savedScope != Scope)
                throw new SyncTransferException("Google 다시 로그인으로 파일 관리 권한을 승인하세요. 기존 읽기 전용 계정은 전송할 수 없습니다.", SyncFailureKind.Authentication);
            TokenResponse token;
            try { token = JsonConvert.DeserializeObject<TokenResponse>(stored) ?? throw new JsonException(); }
            catch (JsonException) { throw new InvalidOperationException("저장된 Google 인증 정보를 읽을 수 없습니다. 인증 정보를 수정하세요."); }
            if (string.IsNullOrWhiteSpace(token.RefreshToken)) throw new InvalidOperationException("Google 갱신 토큰이 없습니다. 인증 정보를 수정하세요.");
            var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer { ClientSecrets = secrets, Scopes = [Scope] });
            credential = new UserCredential(flow, "mysync", token);
        }
        else
        {
            // Explicit memory-only store: the SDK default FileDataStore would write tokens in plaintext.
            credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(secrets, [Scope], Guid.NewGuid().ToString("N"), ct,
                new MemoryStore(), new LocalServerCodeReceiver("<html><body>Login received. Return to MYSync.</body></html>",
                    LocalServerCodeReceiver.CallbackUriChooserStrategy.ForceLoopbackIp));
        }
        var session = new GoogleSession(credential);
        try
        {
            await session.AccessTokenAsync(ct);
            if (credential.Token.Scope is { Length: > 0 } granted && !granted.Split(' ').Contains(Scope, StringComparer.Ordinal))
                throw new SyncTransferException("Google 파일 관리 권한이 승인되지 않았습니다. 다시 로그인하세요.", SyncFailureKind.Authentication);
            if (string.IsNullOrWhiteSpace(credential.Token.RefreshToken)) throw new InvalidOperationException("Google 갱신 토큰을 받지 못했습니다. Google 계정에서 기존 앱 권한을 해제한 뒤 다시 연결하세요.");
            return session;
        }
        catch { session.Dispose(); throw; }
    }
    public async Task<string> AccessTokenAsync(CancellationToken ct)
    {
        try { return await credential.GetAccessTokenForRequestAsync(cancellationToken: ct); }
        catch (TokenResponseException) { throw new SyncTransferException("Google 인증이 만료되었거나 거부되었습니다. 인증 정보를 수정해 다시 로그인하세요.", SyncFailureKind.Authentication); }
    }
    public string ExportToken() => JsonConvert.SerializeObject(credential.Token);
    public void Dispose() => credential.Flow.Dispose();

    private sealed class MemoryStore : IDataStore
    {
        private readonly Dictionary<string, string> values = [];
        public Task StoreAsync<T>(string key, T value) { values[key] = JsonConvert.SerializeObject(value); return Task.CompletedTask; }
        public Task DeleteAsync<T>(string key) { values.Remove(key); return Task.CompletedTask; }
        public Task<T> GetAsync<T>(string key) => Task.FromResult(values.TryGetValue(key, out var value) ? JsonConvert.DeserializeObject<T>(value)! : default!);
        public Task ClearAsync() { values.Clear(); return Task.CompletedTask; }
    }
}

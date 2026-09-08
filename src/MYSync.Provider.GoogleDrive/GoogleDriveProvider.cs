using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MYSync.Provider.Abstractions;
using MYSync.Sync.Core;

namespace MYSync.Provider.GoogleDrive;

/// <summary>First stage: OAuth and My Drive folder browsing. Does not claim transfer support.</summary>
public sealed class GoogleDriveProvider : IProvider, IConfigurableProvider, IPersistableConnectionProvider, IBrowserLoginProvider
{
    private const string FolderType = "application/vnd.google-apps.folder";
    private readonly Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<IGoogleSession>> connect;
    private readonly Func<HttpMessageHandler> handlers;
    private readonly SemaphoreSlim gate = new(1, 1);
    private IGoogleSession? session;
    private HttpClient? http;
    private Dictionary<string, string>? settings;
    private readonly HashSet<string> allowed = new(StringComparer.Ordinal);
    public GoogleDriveProvider() : this(GoogleSession.ConnectAsync, () => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { }
    public GoogleDriveProvider(Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<IGoogleSession>> connect, Func<HttpMessageHandler> handlers)
    { this.connect = connect; this.handlers = handlers; }
    public string Id => "mysync.googledrive";
    public string DisplayName => "Google Drive (연결·탐색)";
    public ProviderCapabilities Capabilities => ProviderCapabilities.None;
    public bool IsConnected => session is not null;
    public IReadOnlyList<ConnectionField> ConnectionFields => [];
    public string LoginButtonText => "Google로 로그인";
    public string ConnectionInstructions => "Google로 로그인을 누르면 브라우저에서 계정과 접근 권한을 선택합니다. 현재는 폴더 탐색만 지원합니다.";
    public async Task ConnectAsync(IReadOnlyDictionary<string, string> values, CancellationToken ct)
    {
        // Saved accounts retain their original OAuth client binding. New logins use the distributor configuration.
        if (!values.ContainsKey("client_id")) values = OAuthClientConfiguration.Load(Path.Combine(
            Path.GetDirectoryName(typeof(GoogleDriveProvider).Assembly.Location)!, "oauth-client.json"));
        if (!values.TryGetValue("client_id", out var id) || string.IsNullOrWhiteSpace(id) || !id.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal)
            || id.Any(char.IsWhiteSpace) || !values.TryGetValue("client_secret", out var secret) || string.IsNullOrWhiteSpace(secret))
            throw new ArgumentException("Google 데스크톱 OAuth 클라이언트 ID와 보안 비밀번호를 입력하세요.");
        await gate.WaitAsync(ct);
        IGoogleSession? candidate = null; HttpClient? client = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(3));
            candidate = await connect(values, timeout.Token);
            client = new HttpClient(handlers()) { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
            var root = await ReadFolder(client, candidate, "root", timeout.Token);
            var saved = new Dictionary<string, string> { ["client_id"] = id, ["client_secret"] = secret, ["oauth_token"] = candidate.ExportToken() };
            session?.Dispose(); http?.Dispose();
            session = candidate; http = client; settings = saved; candidate = null; client = null;
            allowed.Clear(); allowed.Add("root"); allowed.Add(root.Id);
        }
        catch (Google.Apis.Auth.OAuth2.Responses.TokenResponseException)
        { throw new SyncTransferException("Google 로그인이 거부되었습니다. OAuth 클라이언트와 테스트 사용자 설정을 확인하세요.", SyncFailureKind.Authentication); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new InvalidOperationException("Google 연결 대기 시간이 초과되었습니다. 브라우저 로그인을 확인하고 다시 연결하세요."); }
        finally { candidate?.Dispose(); client?.Dispose(); gate.Release(); }
    }
    public IReadOnlyDictionary<string, string> ExportConnectionValues()
    {
        if (settings is null || session is null) throw new InvalidOperationException("Google 계정에 먼저 연결하세요.");
        return new Dictionary<string, string>(settings) { ["oauth_token"] = session.ExportToken() };
    }
    public async Task<IReadOnlyList<RemoteFolder>> GetFoldersAsync(string? parentId, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var active = session ?? throw new InvalidOperationException("Google 계정에 먼저 연결하세요.");
            var client = http!; var id = parentId ?? "root";
            if (!allowed.Contains(id)) throw new InvalidOperationException("내 드라이브에서 탐색한 폴더만 열 수 있습니다.");
            var parent = await ReadFolder(client, active, id, ct);
            var folders = new List<RemoteFolder> { new(parent.Id, parent.Name, null) };
            var ids = new HashSet<string>(StringComparer.Ordinal) { parent.Id };
            var pages = new HashSet<string>(StringComparer.Ordinal);
            string? page = null;
            do
            {
                var query = $"'{Escape(parent.Id)}' in parents and mimeType='{FolderType}' and trashed=false";
                var url = "files?q=" + Uri.EscapeDataString(query) + "&corpora=user&spaces=drive&pageSize=1000&fields="
                    + Uri.EscapeDataString("nextPageToken,incompleteSearch,files(id,name,mimeType,trashed,driveId,parents)")
                    + (page is null ? "" : "&pageToken=" + Uri.EscapeDataString(page));
                using var doc = await Get(client, active, url, ct);
                var body = doc.RootElement;
                if (body.TryGetProperty("incompleteSearch", out var incomplete) && incomplete.GetBoolean()) throw new InvalidDataException("Google 폴더 검색이 불완전합니다. 다시 시도하세요.");
                foreach (var item in body.GetProperty("files").EnumerateArray())
                {
                    var folder = ParseFolder(item);
                    if (!item.TryGetProperty("parents", out var parents) || !parents.EnumerateArray().Any(x => x.GetString() == parent.Id))
                        throw new InvalidDataException("Google 폴더가 요청한 상위 폴더에 속하지 않습니다.");
                    if (!ids.Add(folder.Id)) throw new InvalidDataException("중복 Google 폴더 ID가 있습니다.");
                    folders.Add(new(folder.Id, folder.Name, parent.Id));
                }
                page = body.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
                if (page is not null && (!pages.Add(page) || pages.Count > 100 || folders.Count > 100000)) throw new InvalidDataException("Google 폴더 조회 한도를 초과했습니다.");
            } while (!string.IsNullOrEmpty(page));
            foreach (var folder in folders) allowed.Add(folder.Id);
            return folders.Take(1).Concat(folders.Skip(1).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Id, StringComparer.Ordinal)).ToArray();
        }
        finally { gate.Release(); }
    }
    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");
    private static (string Id, string Name) ParseFolder(JsonElement item)
    {
        if (item.GetProperty("mimeType").GetString() != FolderType || item.TryGetProperty("trashed", out var trash) && trash.GetBoolean()
            || item.TryGetProperty("driveId", out _)) throw new InvalidDataException("휴지통 또는 공유 드라이브 폴더는 현재 지원하지 않습니다.");
        var id = item.GetProperty("id").GetString(); var name = item.GetProperty("name").GetString();
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("Google 폴더 정보가 비었습니다.");
        return (id, name);
    }
    private static async Task<(string Id, string Name)> ReadFolder(HttpClient http, IGoogleSession session, string id, CancellationToken ct)
    {
        using var doc = await Get(http, session, "files/" + Uri.EscapeDataString(id) + "?fields=id,name,mimeType,trashed,driveId", ct);
        var folder = ParseFolder(doc.RootElement);
        if (id != "root" && folder.Id != id) throw new InvalidDataException("요청한 Google 폴더 ID와 응답이 다릅니다.");
        return folder;
    }
    private static async Task<JsonDocument> Get(HttpClient client, IGoogleSession session, string relative, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/drive/v3/" + relative);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await session.AccessTokenAsync(ct));
        using var response = await client.SendAsync(request, ct);
        SyncDiagnostics.Write("googledrive.response", "GET", (int)response.StatusCode);
        if (!response.IsSuccessStatusCode)
            throw new SyncTransferException($"Google Drive 요청 실패 (HTTP {(int)response.StatusCode}). 인증·Drive API 활성화·권한을 확인하세요.", response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => SyncFailureKind.Authentication,
                HttpStatusCode.Forbidden => SyncFailureKind.Permission,
                HttpStatusCode.NotFound => SyncFailureKind.Missing,
                HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout => SyncFailureKind.Transient,
                _ when (int)response.StatusCode >= 500 => SyncFailureKind.Transient,
                _ => SyncFailureKind.Unknown
            });
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }
    public void Dispose() { session?.Dispose(); http?.Dispose(); session = null; http = null; settings?.Clear(); settings = null; allowed.Clear(); }
}

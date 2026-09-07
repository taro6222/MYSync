using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using MYSync.Provider.Abstractions;

namespace MYSync.Provider.WebDav;

public sealed class WebDavException(string message, HttpStatusCode? status = null) : IOException(message)
{
    public HttpStatusCode? Status { get; } = status;
}
public sealed class WebDavProvider : IProvider, IConfigurableProvider
{
    private readonly Func<HttpMessageHandler> handlerFactory;
    private HttpClient? client;
    private Uri? root;
    public WebDavProvider() : this(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { }
    public WebDavProvider(Func<HttpMessageHandler> handlerFactory) => this.handlerFactory = handlerFactory;
    public string Id => "mysync.webdav";
    public string DisplayName => "WebDAV";
    public ProviderCapabilities Capabilities => ProviderCapabilities.None;
    public bool IsConnected => client is not null;
    public IReadOnlyList<ConnectionField> ConnectionFields => [new("url", "서버 주소 (예: nas.example.com/webdav/)"), new("port", "포트", DefaultValue: "443"), new("username", "사용자 이름"), new("password", "비밀번호 / 앱 비밀번호", true)];
    public async Task ConnectAsync(IReadOnlyDictionary<string, string> values, CancellationToken ct)
    {
        values.TryGetValue("url", out var address);
        address = address?.Trim();
        if (!string.IsNullOrWhiteSpace(address) && !address.Contains("://", StringComparison.Ordinal)) address = "https://" + address;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("계정·쿼리·프래그먼트를 포함하지 않는 HTTPS 폴더 주소가 필요합니다.");
        if (!values.TryGetValue("username", out var username) || string.IsNullOrWhiteSpace(username) || username.Contains(':') || username.Any(char.IsControl) ||
            !values.TryGetValue("password", out var password) || password.Any(char.IsControl)) throw new ArgumentException("사용자 이름과 비밀번호를 확인하세요.");
        var builder = new UriBuilder(uri);
        if (values.TryGetValue("port", out var portText) && !string.IsNullOrWhiteSpace(portText))
        {
            if (!int.TryParse(portText, out var port) || port is < 1 or > 65535) throw new ArgumentException("포트는 1~65535 사이의 숫자로 입력하세요.");
            builder.Port = port;
        }
        var candidateRoot = new Uri(builder.Uri.AbsoluteUri.TrimEnd('/') + "/");
        var candidate = new HttpClient(handlerFactory()) { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
        candidate.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password)));
        try
        {
            await List(candidate, candidateRoot, candidateRoot, ct);
            var old = client; client = candidate; root = candidateRoot; old?.Dispose();
        }
        catch { candidate.Dispose(); throw; }
    }
    public Task<IReadOnlyList<RemoteFolder>> GetFoldersAsync(string? parentId, CancellationToken ct)
    {
        var active = client ?? throw new InvalidOperationException("먼저 WebDAV 계정에 연결하세요.");
        var scope = root!;
        var target = parentId is null ? scope : Validate(scope, parentId);
        return List(active, scope, target, ct);
    }
    private static async Task<IReadOnlyList<RemoteFolder>> List(HttpClient client, Uri scope, Uri target, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), target);
        request.Headers.Add("Depth", "1");
        request.Content = new StringContent("<d:propfind xmlns:d=\"DAV:\"><d:prop><d:resourcetype/><d:displayname/></d:prop></d:propfind>", Encoding.UTF8, "application/xml");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        if (response.StatusCode != HttpStatusCode.MultiStatus)
            throw new WebDavException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "WebDAV 인증에 실패했습니다.",
                HttpStatusCode.Forbidden => "원격 폴더에 접근할 권한이 없습니다.",
                HttpStatusCode.NotFound => "원격 폴더가 없습니다.",
                _ => $"폴더 조회 실패 (HTTP {(int)response.StatusCode}). 리디렉션은 자동으로 따라가지 않습니다."
            }, response.StatusCode);
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var reader = XmlReader.Create(body, new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
        var doc = await XDocument.LoadAsync(reader, LoadOptions.None, ct);
        XNamespace dav = "DAV:";
        if (doc.Root?.Name != dav + "multistatus") throw new WebDavException("WebDAV 목록 형식이 올바르지 않습니다.");
        var result = new List<RemoteFolder>(); var seen = new HashSet<string>(StringComparer.Ordinal); var foundSelf = false;
        foreach (var entry in doc.Root.Elements(dav + "response"))
        {
            ct.ThrowIfCancellationRequested();
            var href = entry.Element(dav + "href")?.Value ?? throw new WebDavException("목록 항목의 주소가 없습니다.");
            var resource = Validate(scope, new Uri(target, href).AbsoluteUri);
            if (!seen.Add(resource.AbsoluteUri.TrimEnd('/'))) throw new WebDavException("중복된 원격 항목이 있습니다.");
            var self = resource.AbsolutePath.TrimEnd('/') == target.AbsolutePath.TrimEnd('/');
            var childPath = resource.AbsolutePath.TrimEnd('/');
            if (!self && childPath[..(childPath.LastIndexOf('/') + 1)] != target.AbsolutePath) throw new WebDavException("요청 범위 밖의 목록 항목입니다.");
            var itemStatus = entry.Element(dav + "status")?.Value;
            if (itemStatus is not null && !Success(itemStatus)) throw new WebDavException("일부 원격 항목을 조회하지 못했습니다.");
            var props = entry.Elements(dav + "propstat").Where(p => Success(p.Element(dav + "status")?.Value))
                .SelectMany(p => p.Elements(dav + "prop")).ToArray();
            var types = props.Elements(dav + "resourcetype").ToArray();
            if (types.Length != 1) throw new WebDavException("원격 항목 유형을 확인하지 못했습니다.");
            var isFolder = types[0].Element(dav + "collection") is not null;
            if (self) { if (!isFolder) throw new WebDavException("선택한 주소는 폴더가 아닙니다."); foundSelf = true; }
            if (!isFolder) continue;
            var canonical = resource.AbsoluteUri.TrimEnd('/') + "/";
            var name = props.Elements(dav + "displayname").FirstOrDefault()?.Value;
            if (string.IsNullOrWhiteSpace(name)) name = Uri.UnescapeDataString(resource.AbsolutePath.TrimEnd('/').Split('/').Last());
            result.Add(new(canonical, string.IsNullOrWhiteSpace(name) ? "/" : name, self ? null : target.AbsoluteUri));
        }
        if (!foundSelf) throw new WebDavException("조회한 폴더 자체의 상태가 누락되었습니다.");
        return result.OrderBy(x => x.ParentId is null ? 0 : 1).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static bool Success(string? status)
    {
        var parts = status?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts is { Length: >= 2 } && parts[0].StartsWith("HTTP/", StringComparison.Ordinal) && int.TryParse(parts[1], out var code) && code is >= 200 and < 300;
    }
    private static Uri Validate(Uri scope, string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != scope.Scheme || uri.Host != scope.Host || uri.Port != scope.Port || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new WebDavException("원격 주소가 연결 범위 밖입니다.");
        var path = uri.AbsolutePath;
        if (!(path.TrimEnd('/') == scope.AbsolutePath.TrimEnd('/') || path.StartsWith(scope.AbsolutePath, StringComparison.Ordinal))) throw new WebDavException("원격 경로가 연결 범위 밖입니다.");
        foreach (var segment in path.Split('/'))
        {
            var decoded = Uri.UnescapeDataString(segment);
            if (decoded.Contains('/') || decoded.Contains('\\') || decoded is "." or ".." || decoded.Any(char.IsControl)) throw new WebDavException("지원하지 않는 원격 경로입니다.");
        }
        return uri;
    }
    public void Dispose() { client?.Dispose(); client = null; root = null; }
}


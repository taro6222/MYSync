using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MYSync.Sync.Core;

namespace MYSync.Provider.GoogleDrive;

public sealed partial class GoogleDriveProvider
{
    public ISyncEndpoint OpenEndpoint(string remoteFolderId)
    {
        if (session is null || http is null) throw new InvalidOperationException("Google 계정에 먼저 연결하세요.");
        if (string.IsNullOrWhiteSpace(remoteFolderId) || remoteFolderId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))
            throw new ArgumentException("Google 폴더 ID가 올바르지 않습니다.");
        return new DriveEndpoint(http, session, remoteFolderId);
    }
    private sealed class DriveEndpoint(HttpClient client, IGoogleSession account, string root) : ISyncEndpoint, ISyncPolicyEndpoint, IFileStateEndpoint
    {
        public SyncPolicy Policy { get; set; } = new();
        private const long MultipartThreshold = 5 * 1024 * 1024;
        private const string Fields = "id,name,mimeType,parents,trashed,driveId,version,size,sha256Checksum";
        private sealed record Item(string Id, string Name, string Mime, string[] Parents, string Version, long Size, bool Trashed, bool Shared, string? Tag = null, string? Sha256 = null, bool LegacyTag = false)
        { public bool Folder => Mime == FolderType; }
        private static FileStream Temp() => new(Path.Combine(Path.GetTempPath(), "mysync-drive-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        private static Item Parse(JsonElement item, string? tag = null) => Valid(new(
            item.GetProperty("id").GetString() ?? throw new InvalidDataException("Google ID 누락"),
            item.GetProperty("name").GetString() ?? throw new InvalidDataException("Google 이름 누락"),
            item.GetProperty("mimeType").GetString() ?? throw new InvalidDataException("Google 유형 누락"),
            item.TryGetProperty("parents", out var parents) ? parents.EnumerateArray().Select(x => x.GetString()!).ToArray() : [],
            item.GetProperty("version").GetString() ?? throw new InvalidDataException("Google 버전 누락"),
            item.TryGetProperty("size", out var size) && long.TryParse(size.GetString(), out var length) ? length : 0,
            item.TryGetProperty("trashed", out var trash) && trash.GetBoolean(), item.TryGetProperty("driveId", out _), tag, item.TryGetProperty("sha256Checksum", out var checksum) ? checksum.GetString()?.ToUpperInvariant() : null));
        private static Item Valid(Item item)
        {
            if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Name) || !ulong.TryParse(item.Version, out _) || item.Size < 0)
                throw new InvalidDataException("Google 항목 ID·이름·버전·크기가 올바르지 않습니다.");
            return item;
        }
        private static string? Unsupported(Item item)
        {
            if (item.Shared) return "공유 드라이브는 아직 지원하지 않습니다.";
            if (!item.Folder && item.Mime.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal)) return "Google 문서·바로가기는 동기화하지 않습니다.";
            return null;
        }
        private async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct, bool streaming = false, bool allowIncomplete = false)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await account.AccessTokenAsync(ct));
            var response = await client.SendAsync(request, streaming ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, ct);
            SyncDiagnostics.Write("googledrive.response", request.Method.Method, (int)response.StatusCode, response.Headers.ETag is { IsWeak: false });
            if (allowIncomplete && (int)response.StatusCode == 308) return response;
            if (!response.IsSuccessStatusCode)
            {
                var status = response.StatusCode; response.Dispose();
                throw new SyncTransferException($"Google Drive 전송 실패 (HTTP {(int)status}). 권한과 현재 상태를 확인하세요.", status switch
                {
                    HttpStatusCode.Unauthorized => SyncFailureKind.Authentication, HttpStatusCode.Forbidden => SyncFailureKind.Permission,
                    HttpStatusCode.NotFound => SyncFailureKind.Missing, HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict => SyncFailureKind.Precondition,
                    HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout => SyncFailureKind.Transient,
                    _ when (int)status >= 500 => SyncFailureKind.Transient, _ => SyncFailureKind.Unknown
                });
            }
            return response;
        }
        private async Task<Item> Metadata(string id, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/drive/v3/files/" + Uri.EscapeDataString(id) + "?fields=" + Fields);
            using var response = await Send(request, ct); using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var item = Parse(doc.RootElement, response.Headers.ETag is { IsWeak: false } tag && tag.Tag != "*" ? tag.ToString() : null);
            if (item.Id != id && id != "root" || item.Trashed || item.Shared) throw new SyncPreconditionException("Google 항목이 이동·삭제되었거나 지원 범위 밖입니다.");
            return item;
        }
        private async Task<List<Item>> Children(string id, CancellationToken ct)
        {
            var result = new List<Item>(); var ids = new HashSet<string>(StringComparer.Ordinal); var pages = new HashSet<string>(); string? page = null;
            do
            {
                var query = Uri.EscapeDataString($"'{Escape(id)}' in parents and trashed=false");
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/drive/v3/files?q=" + query + "&corpora=user&spaces=drive&pageSize=1000&fields="
                    + Uri.EscapeDataString("nextPageToken,incompleteSearch,files(" + Fields + ")") + (page is null ? "" : "&pageToken=" + Uri.EscapeDataString(page)));
                using var response = await Send(request, ct); using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.TryGetProperty("incompleteSearch", out var incomplete) && incomplete.GetBoolean()) throw new InvalidDataException("Google 검색 결과가 불완전합니다.");
                foreach (var value in doc.RootElement.GetProperty("files").EnumerateArray())
                {
                    var item = Parse(value);
                    if (!ids.Add(item.Id) || !item.Parents.Contains(id) || item.Trashed) throw new InvalidDataException("Google 목록 ID·상위 폴더 오류");
                    result.Add(item);
                    if (result.Count > 100000) throw new InvalidDataException("Google 항목 한도 초과");
                }
                page = doc.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
                if (!string.IsNullOrEmpty(page) && (!pages.Add(page) || pages.Count > 100)) throw new InvalidDataException("Google 페이지 조회 한도 초과");
            } while (!string.IsNullOrEmpty(page));
            return result;
        }
        public async Task<ScanResult> ScanAsync(CancellationToken ct)
        {
            var entries = new List<SyncEntry>(); var notices = new List<UnsupportedItem>(); var visited = new HashSet<string>();
            try
            {
                var folder = await Metadata(root, ct); if (!folder.Folder) throw new SyncPreconditionException("Google 루트가 폴더가 아닙니다.");
                await Visit(folder.Id, "", 0); return new(entries, [], notices);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or JsonException or HttpRequestException or KeyNotFoundException)
            { return new(entries, ["Google 검사 실패: " + ex.Message], notices); }
            async Task Visit(string id, string prefix, int depth)
            {
                ct.ThrowIfCancellationRequested();
                if (depth > 128 || !visited.Add(id) || visited.Count + entries.Count > 100000) throw new InvalidDataException("Google 폴더 순환 또는 검사 한도 초과");
                foreach (var group in (await Children(id, ct)).GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var item = group.First();
                    if (string.IsNullOrWhiteSpace(item.Name) || item.Name is "." or ".." || item.Name.IndexOfAny(['/', '\\', ':']) >= 0 || item.Name.Any(char.IsControl))
                        throw new InvalidDataException("상대 경로로 표현할 수 없는 Google 이름입니다.");
                    var path = prefix + item.Name;
                    if (Policy.Exclusions.Matches(path, item.Folder ? EntryKind.Directory : EntryKind.File)) { notices.Add(new(path, "연결별 제외 규칙")); continue; }
                    if (group.Count() > 1) { notices.Add(new(path, "같은 이름의 Google 항목이 여러 개입니다.")); continue; }
                    if (Unsupported(item) is { } why) { notices.Add(new(path, why)); continue; }
                    if (item.Folder) { entries.Add(new(path, EntryKind.Directory, null)); await Visit(item.Id, path + "/", depth + 1); }
                    else if (item.Sha256 is { Length: 64 } hash && hash.All(Uri.IsHexDigit)) entries.Add(new(path, EntryKind.File, hash));
                    else { var (stream, downloadedHash) = await Download(item, ct); await stream.DisposeAsync(); entries.Add(new(path, EntryKind.File, downloadedHash)); }
                }
            }
        }
        public bool SupportsConcurrentFiles => true;
        public async Task<SyncEntry?> InspectFileAsync(string path, CancellationToken ct)
        {
            var (_, item) = await Resolve(path, ct);
            if (item is null) return null;
            if (item.Folder) return new(path, EntryKind.Directory, null);
            var (stream, hash) = await Download(item, ct);
            await stream.DisposeAsync();
            return new(path, EntryKind.File, hash);
        }
        private static string[] Parts(string path)
        {
            var parts = path.Split('/');
            if (parts.Any(x => string.IsNullOrWhiteSpace(x) || x is "." or ".." || x.Contains('\\') || x.Contains(':') || x.Any(char.IsControl)))
                throw new SyncPreconditionException("지원하지 않는 상대 경로입니다.");
            return parts;
        }
        private async Task<(Item Parent, Item? Existing)> Resolve(string path, CancellationToken ct)
        {
            var parts = Parts(path); var parent = await Metadata(root, ct);
            if (!parent.Folder) throw new SyncPreconditionException("Google 루트가 폴더가 아닙니다.");
            for (var i = 0; i < parts.Length; i++)
            {
                var matches = (await Children(parent.Id, ct)).Where(x => string.Equals(x.Name, parts[i], StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length > 1) throw new SyncPreconditionException("Google 경로에 중복 이름이 있습니다.");
                var found = matches.SingleOrDefault();
                if (found is not null && (found.Name != parts[i] || Unsupported(found) is not null)) throw new SyncPreconditionException("Google 경로의 이름·유형을 처리할 수 없습니다.");
                if (i == parts.Length - 1) return (parent, found);
                if (found is null || !found.Folder) throw new SyncPreconditionException("Google 상위 폴더가 없습니다.");
                parent = await Metadata(found.Id, ct);
            }
            throw new InvalidOperationException();
        }
        private static bool Same(Item a, Item b) => a.Id == b.Id && a.Version == b.Version && a.Name == b.Name && a.Mime == b.Mime && a.Parents.SequenceEqual(b.Parents);
        private async Task<(FileStream Stream, string Hash)> Download(Item expected, CancellationToken ct)
        {
            if (expected.Folder || Unsupported(expected) is not null) throw new SyncPreconditionException("다운로드할 수 없는 Google 항목입니다.");
            var before = await Metadata(expected.Id, ct);
            if (!Same(before, expected)) throw new SyncPreconditionException("Google 파일이 검사 이후 변경되었습니다.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(Policy.Bandwidth.TimeoutFor(expected.Size));
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/drive/v3/files/" + Uri.EscapeDataString(expected.Id) + "?alt=media");
            request.Options.Set(RequestTimeoutHandler.Streaming, true);
            using var response = await Send(request, timeout.Token, true); var stream = Temp();
            try
            {
                var buffer = new byte[65536]; await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                int read;
                while ((read = await input.ReadAsync(buffer, timeout.Token)) != 0)
                { if (stream.Length + read > expected.Size) throw new SyncPreconditionException("다운로드 크기가 검사한 파일 크기와 다릅니다."); await Policy.Bandwidth.WaitAsync(read, timeout.Token); await stream.WriteAsync(buffer.AsMemory(0, read), timeout.Token); }
                if (stream.Length != expected.Size) throw new SyncPreconditionException("불완전한 다운로드입니다.");
                stream.Position = 0; var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, timeout.Token)); stream.Position = 0;
                if (!Same(before, await Metadata(expected.Id, timeout.Token))) throw new SyncPreconditionException("다운로드 중 Google 파일이 변경되었습니다.");
                return (stream, hash);
            }
            catch { await stream.DisposeAsync(); throw; }
        }
        public async Task<Stream> OpenReadAsync(SyncEntry expected, CancellationToken ct)
        {
            var (_, item) = await Resolve(expected.Path, ct);
            if (item is null || expected.Kind != EntryKind.File) throw new SyncPreconditionException("Google 파일이 없습니다.");
            var (stream, hash) = await Download(item, ct);
            if (hash == expected.ContentHash) return stream;
            await stream.DisposeAsync(); throw new SyncPreconditionException("Google 파일 내용이 변경되었습니다.");
        }
        private async Task<Item> Verify(Item item, SyncEntry expected, CancellationToken ct)
        {
            if (expected.Kind != EntryKind.File) throw new SyncPreconditionException("Google 파일 유형이 다릅니다.");
            var (stream, hash) = await Download(item, ct); await stream.DisposeAsync();
            var current = await Metadata(item.Id, ct);
            if (hash != expected.ContentHash || !Same(item, current)) throw new SyncPreconditionException("Google 대상 내용이 변경되었습니다.");
            if (current.Tag is null)
            {
                // Drive v3 commonly omits ETag. v2 exposes the resource ETag in its JSON.
                // Match the same file version before using it with a v2 conditional mutation.
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/drive/v2/files/" + Uri.EscapeDataString(item.Id) + "?fields=id,version,etag");
                using var response = await Send(request, ct);
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var value = doc.RootElement;
                var tag = value.TryGetProperty("etag", out var etag) ? etag.GetString() : null;
                if (!value.TryGetProperty("id", out var id) || id.GetString() != item.Id ||
                    !value.TryGetProperty("version", out var version) || version.GetString() != current.Version ||
                    !EntityTagHeaderValue.TryParse(tag, out var parsed) || parsed.IsWeak || parsed.Tag == "*" ||
                    !Same(current, await Metadata(item.Id, ct)))
                    throw new SyncPreconditionException("Google 파일 버전 또는 조건부 변경 정보를 확인할 수 없습니다. 다시 검사합니다.");
                current = current with { Tag = parsed.ToString(), LegacyTag = true };
            }
            return current;
        }
        private async Task<string> GenerateId(CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/drive/v3/files/generateIds?count=1&space=drive&type=files");
            using var response = await Send(request, ct); using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return doc.RootElement.GetProperty("ids").EnumerateArray().Single().GetString()!;
        }
        public async Task PutFileAsync(string path, SyncEntry? expected, Stream content, string sha256, CancellationToken ct)
        {
            await using var data = Temp(); var buffer = new byte[65536]; int n;
            while ((n = await content.ReadAsync(buffer, ct)) != 0)
            { await data.WriteAsync(buffer.AsMemory(0, n), ct); }
            data.Position = 0;
            if (Convert.ToHexString(await SHA256.HashDataAsync(data, ct)) != sha256) throw new SyncPreconditionException("업로드 해시 불일치");
            data.Position = 0;
            var (parent, existing) = await Resolve(path, ct);
            if (expected is null && existing is not null || expected is not null && (existing is null || expected.Path != path)) throw new SyncPreconditionException("Google 업로드 대상이 변경되었습니다.");
            if (existing is not null) existing = await Verify(existing, expected!, ct);
            var id = existing?.Id ?? await GenerateId(ct);
            if (data.Length > MultipartThreshold) await UploadChunks(data, parent, existing, id, path, ct);
            else
            {
            using var body = new MultipartContent("related");
            body.Add(new StringContent(JsonSerializer.Serialize(existing is null ? new { id, name = Parts(path)[^1], parents = new[] { parent.Id } } : (object)new { }), Encoding.UTF8, "application/json"));
            var media = new StreamContent(data); media.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream"); body.Add(media);
            using var request = new HttpRequestMessage(existing is null ? HttpMethod.Post : existing.LegacyTag ? HttpMethod.Put : HttpMethod.Patch,
                "https://www.googleapis.com/upload/drive/" + (existing?.LegacyTag == true ? "v2" : "v3") + "/files" + (existing is null ? "" : "/" + Uri.EscapeDataString(id)) + "?uploadType=multipart&fields=id");
            request.Content = Policy.Bandwidth.Limit(body);
            request.Options.Set(RequestTimeoutHandler.Budget, Policy.Bandwidth.TimeoutFor(data.Length + 4096));
            if (existing is not null) request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(existing.Tag!));
            using var response = await Send(request, ct);
            }
            var (_, published) = await Resolve(path, ct);
            if (published?.Id != id) throw new SyncPreconditionException("게시 이후 Google 이름 충돌 또는 이동을 확인해야 합니다.");
            var (check, hash) = await Download(published, ct); await check.DisposeAsync();
            if (hash != sha256) throw new SyncPreconditionException("게시된 Google 파일 내용이 다릅니다.");
        }
        private async Task UploadChunks(FileStream data, Item parent, Item? existing, string id, string path, CancellationToken ct)
        {
            using var initiate = new HttpRequestMessage(existing is null ? HttpMethod.Post : existing.LegacyTag ? HttpMethod.Put : HttpMethod.Patch,
                "https://www.googleapis.com/upload/drive/" + (existing?.LegacyTag == true ? "v2" : "v3") + "/files" + (existing is null ? "" : "/" + Uri.EscapeDataString(id)) + "?uploadType=resumable");
            initiate.Content = new StringContent(JsonSerializer.Serialize(existing is null ? new { id, name = Parts(path)[^1], parents = new[] { parent.Id } } : (object)new { }), Encoding.UTF8, "application/json");
            initiate.Headers.TryAddWithoutValidation("X-Upload-Content-Type", "application/octet-stream");
            initiate.Headers.TryAddWithoutValidation("X-Upload-Content-Length", data.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (existing is not null) initiate.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(existing.Tag!));
            using var start = await Send(initiate, ct);
            var location = start.Headers.Location;
            if (location is null || !location.IsAbsoluteUri || location.Scheme != "https" || location.Host != "www.googleapis.com" ||
                location.Port != 443 || location.UserInfo.Length != 0 || !(location.AbsolutePath.StartsWith("/upload/drive/v3/files", StringComparison.Ordinal) || location.AbsolutePath.StartsWith("/upload/drive/v2/files", StringComparison.Ordinal)))
                throw new SyncPreconditionException("Google 업로드 세션 주소가 올바르지 않습니다.");
            var buffer = new byte[8 * 1024 * 1024];
            long offset = 0;
            while (offset < data.Length)
            {
                var count = (int)Math.Min(buffer.Length, data.Length - offset);
                await data.ReadExactlyAsync(buffer.AsMemory(0, count), ct);
                if (existing is not null && offset + count == data.Length && !Same(existing, await Metadata(existing.Id, ct)))
                    throw new SyncPreconditionException("업로드 중 원격 파일이 변경되었습니다.");
                using var chunk = new HttpRequestMessage(HttpMethod.Put, location);
                var content = new ByteArrayContent(buffer, 0, count);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + count - 1, data.Length);
                chunk.Content = Policy.Bandwidth.Limit(content);
                chunk.Options.Set(RequestTimeoutHandler.Budget, Policy.Bandwidth.TimeoutFor(count));
                if (existing is not null) chunk.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(existing.Tag!));
                using var response = await Send(chunk, ct, allowIncomplete: true);
                offset += count;
                if (offset < data.Length)
                {
                    if ((int)response.StatusCode != 308 || !response.Headers.TryGetValues("Range", out var ranges) ||
                        ranges.SingleOrDefault() != "bytes=0-" + (offset - 1))
                        throw new SyncPreconditionException("Google 업로드 진행 범위를 확인할 수 없습니다.");
                }
                else if (!response.IsSuccessStatusCode) throw new SyncPreconditionException("Google 업로드 완료를 확인할 수 없습니다.");
            }
        }
        public async Task CreateDirectoryAsync(string path, CancellationToken ct)
        {
            var (parent, existing) = await Resolve(path, ct); if (existing is not null) throw new SyncPreconditionException("Google 항목이 이미 있습니다.");
            var id = await GenerateId(ct);
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/drive/v3/files?fields=id")
            { Content = new StringContent(JsonSerializer.Serialize(new { id, name = Parts(path)[^1], mimeType = FolderType, parents = new[] { parent.Id } }), Encoding.UTF8, "application/json") };
            using var response = await Send(request, ct);
            var (_, created) = await Resolve(path, ct); if (created?.Id != id) throw new SyncPreconditionException("Google 폴더 생성 이후 이름 충돌을 확인하세요.");
        }
        public async Task DeleteAsync(SyncEntry expected, CancellationToken ct)
        {
            if (expected.Kind == EntryKind.Directory) throw new SyncPreconditionException("Google 폴더 삭제는 하위 변경 보호 구현 전까지 중단합니다.");
            var (_, found) = await Resolve(expected.Path, ct); if (found is null) throw new SyncPreconditionException("Google 파일이 없습니다.");
            var verified = await Verify(found, expected, ct);
            using var request = new HttpRequestMessage(HttpMethod.Patch, "https://www.googleapis.com/drive/" + (verified.LegacyTag ? "v2" : "v3") + "/files/" + Uri.EscapeDataString(found.Id) + "?fields=id")
            { Content = new StringContent(verified.LegacyTag ? "{\"labels\":{\"trashed\":true}}" : "{\"trashed\":true}", Encoding.UTF8, "application/json") };
            request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(verified.Tag!));
            using var response = await Send(request, ct);
            if ((await Resolve(expected.Path, ct)).Existing is not null) throw new SyncPreconditionException("Google 휴지통 이동 결과를 재확인하세요.");
        }
    }
}

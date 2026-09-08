using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using MYSync.Sync.Core;

namespace MYSync.Provider.WebDav;

public sealed partial class WebDavProvider
{
    public ISyncEndpoint OpenEndpoint(string remoteFolderId)
    {
        var active = client ?? throw new InvalidOperationException("계정에 먼저 연결하세요.");
        var folder = Validate(root!, remoteFolderId);
        return new DavEndpoint(active, new Uri(folder.AbsoluteUri.TrimEnd('/') + "/"));
    }
    private sealed class DavEndpoint(HttpClient http, Uri scope) : ISyncEndpoint, ISyncPolicyEndpoint
    {
        public SyncPolicy Policy { get; set; } = new();
        private const string StagingPrefix = ".mysync-upload-";
        // Skips re-downloading a file whose strong ETag and length are unchanged since we hashed it in this session.
        // Advisory only: every read, replace and delete downloads and re-verifies the content before acting.
        private readonly ConcurrentDictionary<string, (string ETag, long Length, string Hash)> hashes = new(StringComparer.Ordinal);
        private static FileStream Temp() => new(Path.Combine(Path.GetTempPath(), "mysync-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        private Uri Resolve(string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || relative.Split('/').Any(x => x is "" or "." or ".." || x.Contains('\\') || x.Contains(':') || x.Any(char.IsControl)))
                throw new SyncPreconditionException("잘못된 원격 상대 경로입니다.");
            return Validate(scope, new Uri(scope, string.Join('/', relative.Split('/').Select(Uri.EscapeDataString))).AbsoluteUri);
        }
        public async Task<ScanResult> ScanAsync(CancellationToken ct)
        {
            var entries = new List<SyncEntry>(); var notices = new List<UnsupportedItem>();
            try { await Visit(scope, 0); return new(entries, [], notices); }
            catch (Exception ex) when (ex is IOException or HttpRequestException or System.Xml.XmlException or InvalidOperationException)
            { return new(entries, ["원격 검사 실패: " + ex.Message], notices); }
            async Task Visit(Uri directory, int depth)
            {
                if (depth > 128 || entries.Count > 100000) throw new WebDavException("원격 검사 한도를 초과했습니다.");
                foreach (var item in (await Query(http, scope, directory, ct)).Where(x => !x.IsSelf))
                {
                    var path = string.Join('/', scope.MakeRelativeUri(item.Uri).ToString().TrimEnd('/').Split('/').Select(Uri.UnescapeDataString));
                    Resolve(path);
                    if (Policy.Exclusions.Matches(path, item.IsFolder ? EntryKind.Directory : EntryKind.File)) { notices.Add(new(path, "연결별 제외 규칙")); continue; }
                    if (path.Split('/').Last().StartsWith(StagingPrefix, StringComparison.Ordinal)) throw new WebDavException("완료되지 않은 업로드 파일을 확인하세요: " + path);
                    if (item.IsFolder)
                    {
                        entries.Add(new(path, EntryKind.Directory, null));
                        await Visit(new Uri(item.Uri.AbsoluteUri.TrimEnd('/') + "/"), depth + 1);
                    }
                    else
                    {
                        var strong = ValidStrongTag(item.ETag);
                        if (strong is not null && hashes.TryGetValue(item.Uri.AbsoluteUri, out var known)
                            && known.ETag == strong && (item.Length is null || item.Length == known.Length))
                        { entries.Add(new(path, EntryKind.File, known.Hash)); continue; }
                        var (stream, hash, tag, length) = await Download(item.Uri, ct, knownTag: strong);
                        await stream.DisposeAsync();
                        if (tag is not null) hashes[item.Uri.AbsoluteUri] = (tag, length, hash);
                        entries.Add(new(path, EntryKind.File, hash));
                    }
                }
            }
        }
        private async Task<(FileStream Stream, string Hash, string? ETag, long Length)> Download(Uri uri, CancellationToken ct, bool forMutation = false, string? knownTag = null)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(5));
            if (forMutation)
            {
                var metadata = (await Query(http, scope, uri, timeout.Token, resourceOnly: true)).Single();
                if (metadata.IsFolder) throw new SyncPreconditionException("예상 파일이 폴더로 변경되었습니다.");
                knownTag = ValidStrongTag(metadata.ETag);
                SyncDiagnostics.Write("webdav.resource-etag", "PROPFIND", hasStrongETag: knownTag is not null);
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (knownTag is not null) request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(knownTag));
            request.Options.Set(RequestTimeoutHandler.Streaming, true);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode != HttpStatusCode.OK) throw Failure("다운로드", response.StatusCode);
            var responseTag = response.Headers.ETag;
            if (knownTag is not null && responseTag is not null && responseTag.ToString() != knownTag)
                throw new SyncPreconditionException("조건부 다운로드의 ETag가 조회한 버전과 다릅니다.");
            var stream = Temp();
            try
            {
                timeout.CancelAfter(response.Content.Headers.ContentLength is long size ? Policy.Bandwidth.TimeoutFor(size) : Policy.Bandwidth.BytesPerSecond == 0 ? TimeSpan.FromMinutes(5) : Timeout.InfiniteTimeSpan);
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                await Policy.Bandwidth.CopyAsync(input, stream, timeout.Token); stream.Position = 0;
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, timeout.Token)); stream.Position = 0;
                return (stream, hash, knownTag ?? ValidStrongTag(responseTag?.ToString()), stream.Length);
            }
            catch { await stream.DisposeAsync(); throw; }
        }
        private static WebDavException Failure(string action, HttpStatusCode status) => new(status switch
        {
            HttpStatusCode.Unauthorized => $"{action} 실패: 인증이 거부되었습니다. 저장된 계정의 사용자 이름과 비밀번호를 다시 확인하세요.",
            HttpStatusCode.Forbidden => $"{action} 실패: 서버가 접근을 거부했습니다. 원격 폴더의 쓰기 권한을 확인하세요.",
            HttpStatusCode.NotFound => $"{action} 실패: 원격 항목이 없습니다. 다시 검사하세요.",
            HttpStatusCode.MethodNotAllowed => $"{action} 실패: 서버가 이 동작을 지원하지 않거나 같은 이름의 항목이 이미 있습니다.",
            HttpStatusCode.Conflict => $"{action} 실패: 상위 폴더가 없습니다. 먼저 폴더를 만들어야 합니다.",
            HttpStatusCode.PreconditionFailed => $"{action} 실패: 서버의 항목이 예상과 달라 조건부 요청이 거부되었습니다. 다시 검사한 뒤 실행하세요.",
            HttpStatusCode.RequestEntityTooLarge => $"{action} 실패: 서버가 허용하는 크기를 초과했습니다.",
            HttpStatusCode.Locked => $"{action} 실패: 서버에서 항목이 잠겨 있습니다.",
            HttpStatusCode.InsufficientStorage => $"{action} 실패: 서버 저장 공간이 부족합니다.",
            HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests => $"{action} 실패: 서버가 일시적으로 처리하지 못했습니다. 잠시 후 다시 시도하세요.",
            _ when (int)status >= 500 => $"{action} 실패: 서버 오류 (HTTP {(int)status}). 잠시 후 다시 시도하세요.",
            _ when (int)status is >= 300 and < 400 => $"{action} 실패: 서버가 다른 주소로 이동을 요구했습니다 (HTTP {(int)status}). 최종 WebDAV 폴더 주소를 입력하세요.",
            _ => $"{action} 실패 (HTTP {(int)status}). 조건 불일치·권한·연결 상태를 확인하세요."
        }, status);
        private static string? ValidStrongTag(string? value) =>
            EntityTagHeaderValue.TryParse(value, out var tag) && !tag.IsWeak && tag.Tag != "*" && !tag.Tag.Contains(']') && !tag.Tag.Contains('[')
                ? tag.ToString() : null;
        private static string StrongTag(string? tag)
        {
            return ValidStrongTag(tag) ?? throw new SyncPreconditionException("PROPFIND와 GET에서 유효한 강한 ETag를 얻지 못해 조건부 변경을 수행할 수 없습니다.");
        }
        public async Task<Stream> OpenReadAsync(SyncEntry expected, CancellationToken ct)
        {
            if (expected.Kind != EntryKind.File || expected.ContentHash is null) throw new SyncPreconditionException("파일 해시가 필요합니다.");
            var (stream, hash, _, _) = await Download(Resolve(expected.Path), ct);
            if (hash == expected.ContentHash) return stream;
            await stream.DisposeAsync(); throw new SyncPreconditionException("원격 파일이 변경되었습니다.");
        }
        public async Task PutFileAsync(string path, SyncEntry? expected, Stream content, string sha256, CancellationToken ct)
        {
            var destination = Resolve(path); string? destinationTag = null;
            if (expected is not null)
            {
                if (expected.Path != path || expected.Kind != EntryKind.File) throw new SyncPreconditionException("예상 파일 정보가 올바르지 않습니다.");
                var (old, hash, tag, _) = await Download(destination, ct, forMutation: true); await old.DisposeAsync();
                if (hash != expected.ContentHash) throw new SyncPreconditionException("업로드 대상이 변경되었습니다.");
                destinationTag = StrongTag(tag);
            }
            await using var staged = Temp(); await content.CopyToAsync(staged, ct); staged.Position = 0;
            if (Convert.ToHexString(await SHA256.HashDataAsync(staged, ct)) != sha256) throw new SyncPreconditionException("업로드 내용 해시 불일치");
            staged.Position = 0;
            var temporary = new Uri(destination, StagingPrefix + Guid.NewGuid().ToString("N"));
            string? stagingTag = null;
            try
            {
                using (var put = new HttpRequestMessage(HttpMethod.Put, temporary))
                {
                    put.Headers.TryAddWithoutValidation("If-None-Match", "*"); put.Content = Policy.Bandwidth.Limit(new StreamContent(staged));
                    put.Options.Set(RequestTimeoutHandler.Budget, Policy.Bandwidth.TimeoutFor(staged.Length));
                    using var response = await http.SendAsync(put, ct);
                    if (response.StatusCode != HttpStatusCode.Created) throw Failure("임시 업로드", response.StatusCode);
                }
                var (check, uploadedHash, tag, _) = await Download(temporary, ct, forMutation: true); await check.DisposeAsync();
                if (uploadedHash != sha256) throw new SyncPreconditionException("서버에 저장된 업로드 내용이 다릅니다.");
                stagingTag = StrongTag(tag);
                using var move = new HttpRequestMessage(new HttpMethod("MOVE"), temporary);
                move.Headers.TryAddWithoutValidation("Destination", destination.AbsoluteUri);
                move.Headers.TryAddWithoutValidation("Overwrite", expected is null ? "F" : "T");
                move.Headers.TryAddWithoutValidation("If-Match", stagingTag);
                if (destinationTag is not null) move.Headers.TryAddWithoutValidation("If", $"<{destination.AbsoluteUri}> ([{destinationTag}])");
                using var moved = await http.SendAsync(move, ct);
                if (moved.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.NoContent)) throw Failure("업로드 게시", moved.StatusCode);
                hashes.TryRemove(destination.AbsoluteUri, out _);
                stagingTag = null;
            }
            catch
            {
                // Only remove the exact, fully verified temporary file. Unknown/partial uploads remain visible as scan errors.
                if (stagingTag is not null)
                {
                    try
                    {
                        using var cleanup = new HttpRequestMessage(HttpMethod.Delete, temporary); cleanup.Headers.TryAddWithoutValidation("If-Match", stagingTag);
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        using var cleaned = await http.SendAsync(cleanup, timeout.Token);
                        SyncDiagnostics.Write(cleaned.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound ? "webdav.staging-cleaned" : "webdav.staging-cleanup-refused",
                            "DELETE", (int)cleaned.StatusCode);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { }
                }
                throw;
            }
        }
        public async Task CreateDirectoryAsync(string path, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(new HttpMethod("MKCOL"), Resolve(path));
            request.Headers.TryAddWithoutValidation("If-None-Match", "*");
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode != HttpStatusCode.Created) throw Failure("폴더 생성", response.StatusCode);
        }
        public async Task DeleteAsync(SyncEntry expected, CancellationToken ct)
        {
            if (expected.Kind == EntryKind.Directory) throw new SyncPreconditionException("WebDAV 폴더 삭제는 하위 변경 보호 구현 전까지 중단합니다.");
            var target = Resolve(expected.Path); var (stream, hash, tag, _) = await Download(target, ct, forMutation: true); await stream.DisposeAsync();
            if (hash != expected.ContentHash) throw new SyncPreconditionException("삭제 대상이 변경되었습니다.");
            using var request = new HttpRequestMessage(HttpMethod.Delete, target); request.Headers.TryAddWithoutValidation("If-Match", StrongTag(tag));
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode != HttpStatusCode.NoContent) throw Failure("파일 삭제", response.StatusCode);
            hashes.TryRemove(target.AbsoluteUri, out _);
        }
    }
}

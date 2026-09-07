using System.Net;
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
    private sealed class DavEndpoint(HttpClient http, Uri scope) : ISyncEndpoint
    {
        private const string StagingPrefix = ".mysync-upload-";
        private static FileStream Temp() => new(Path.Combine(Path.GetTempPath(), "mysync-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        private Uri Resolve(string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || relative.Split('/').Any(x => x is "" or "." or ".." || x.Contains('\\') || x.Contains(':') || x.Any(char.IsControl)))
                throw new SyncPreconditionException("잘못된 원격 상대 경로입니다.");
            return Validate(scope, new Uri(scope, string.Join('/', relative.Split('/').Select(Uri.EscapeDataString))).AbsoluteUri);
        }
        public async Task<ScanResult> ScanAsync(CancellationToken ct)
        {
            var entries = new List<SyncEntry>();
            try { await Visit(scope, 0); return new(entries, []); }
            catch (Exception ex) when (ex is IOException or HttpRequestException or System.Xml.XmlException or InvalidOperationException)
            { return new(entries, ["원격 검사 실패: " + ex.Message]); }
            async Task Visit(Uri directory, int depth)
            {
                if (depth > 128 || entries.Count > 100000) throw new WebDavException("원격 검사 한도를 초과했습니다.");
                foreach (var item in (await Query(http, scope, directory, ct)).Where(x => !x.IsSelf))
                {
                    var path = string.Join('/', scope.MakeRelativeUri(item.Uri).ToString().TrimEnd('/').Split('/').Select(Uri.UnescapeDataString));
                    Resolve(path);
                    if (path.Split('/').Last().StartsWith(StagingPrefix, StringComparison.Ordinal)) throw new WebDavException("완료되지 않은 업로드 파일을 확인하세요: " + path);
                    if (item.IsFolder)
                    {
                        entries.Add(new(path, EntryKind.Directory, null));
                        await Visit(new Uri(item.Uri.AbsoluteUri.TrimEnd('/') + "/"), depth + 1);
                    }
                    else
                    {
                        var (stream, hash, _) = await Download(item.Uri, ct);
                        await stream.DisposeAsync(); entries.Add(new(path, EntryKind.File, hash));
                    }
                }
            }
        }
        private async Task<(FileStream Stream, string Hash, string? ETag)> Download(Uri uri, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(5));
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode != HttpStatusCode.OK) throw Failure("다운로드", response.StatusCode);
            var stream = Temp();
            try
            {
                await response.Content.CopyToAsync(stream, timeout.Token); stream.Position = 0;
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, timeout.Token)); stream.Position = 0;
                var tag = response.Headers.ETag;
                return (stream, hash, tag is { IsWeak: false } ? tag.ToString() : null);
            }
            catch { await stream.DisposeAsync(); throw; }
        }
        private static WebDavException Failure(string action, HttpStatusCode status) => new($"{action} 실패 (HTTP {(int)status}). 조건 불일치·권한·연결 상태를 확인하세요.", status);
        private static string StrongTag(string? tag)
        {
            if (tag is null || tag.Contains(']') || tag.Contains('[')) throw new SyncPreconditionException("서버의 강한 ETag가 없어 조건부 변경을 수행할 수 없습니다.");
            return tag;
        }
        public async Task<Stream> OpenReadAsync(SyncEntry expected, CancellationToken ct)
        {
            if (expected.Kind != EntryKind.File || expected.ContentHash is null) throw new SyncPreconditionException("파일 해시가 필요합니다.");
            var (stream, hash, _) = await Download(Resolve(expected.Path), ct);
            if (hash == expected.ContentHash) return stream;
            await stream.DisposeAsync(); throw new SyncPreconditionException("원격 파일이 변경되었습니다.");
        }
        public async Task PutFileAsync(string path, SyncEntry? expected, Stream content, string sha256, CancellationToken ct)
        {
            var destination = Resolve(path); string? destinationTag = null;
            if (expected is not null)
            {
                if (expected.Path != path || expected.Kind != EntryKind.File) throw new SyncPreconditionException("예상 파일 정보가 올바르지 않습니다.");
                var (old, hash, tag) = await Download(destination, ct); await old.DisposeAsync();
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
                    put.Headers.TryAddWithoutValidation("If-None-Match", "*"); put.Content = new StreamContent(staged);
                    using var response = await http.SendAsync(put, ct);
                    if (response.StatusCode != HttpStatusCode.Created) throw Failure("임시 업로드", response.StatusCode);
                }
                var (check, uploadedHash, tag) = await Download(temporary, ct); await check.DisposeAsync();
                if (uploadedHash != sha256) throw new SyncPreconditionException("서버에 저장된 업로드 내용이 다릅니다.");
                stagingTag = StrongTag(tag);
                using var move = new HttpRequestMessage(new HttpMethod("MOVE"), temporary);
                move.Headers.TryAddWithoutValidation("Destination", destination.AbsoluteUri);
                move.Headers.TryAddWithoutValidation("Overwrite", expected is null ? "F" : "T");
                move.Headers.TryAddWithoutValidation("If-Match", stagingTag);
                if (destinationTag is not null) move.Headers.TryAddWithoutValidation("If", $"<{destination.AbsoluteUri}> ([{destinationTag}])");
                using var moved = await http.SendAsync(move, ct);
                if (moved.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.NoContent)) throw Failure("업로드 게시", moved.StatusCode);
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
                        using var ignored = await http.SendAsync(cleanup, timeout.Token);
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
            var target = Resolve(expected.Path); var (stream, hash, tag) = await Download(target, ct); await stream.DisposeAsync();
            if (hash != expected.ContentHash) throw new SyncPreconditionException("삭제 대상이 변경되었습니다.");
            using var request = new HttpRequestMessage(HttpMethod.Delete, target); request.Headers.TryAddWithoutValidation("If-Match", StrongTag(tag));
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode != HttpStatusCode.NoContent) throw Failure("파일 삭제", response.StatusCode);
        }
    }
}

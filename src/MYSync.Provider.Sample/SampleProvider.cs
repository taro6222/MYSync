using MYSync.Provider.Abstractions;
namespace MYSync.Provider.Sample;
public sealed class SampleProvider : IProvider
{
    public string Id => "mysync.sample";
    public string DisplayName => "Sample (연결 구조 검증용)";
    public ProviderCapabilities Capabilities => ProviderCapabilities.None;
    public Task<IReadOnlyList<RemoteFolder>> GetFoldersAsync(string? parentId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<RemoteFolder> folders = parentId is null ? [new("sample-root", "샘플 원격 폴더", null)] : [];
        return Task.FromResult(folders);
    }
    public void Dispose() { }
}

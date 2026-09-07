namespace MYSync.Provider.Abstractions;

[Flags]
public enum ProviderCapabilities { None = 0, ChangeTracking = 1, ResumeDownload = 2, ConditionalWrite = 4, Move = 8 }
public sealed record RemoteFolder(string Id, string Name, string? ParentId);
public interface IProvider : IDisposable
{
    string Id { get; }
    string DisplayName { get; }
    ProviderCapabilities Capabilities { get; }
    Task<IReadOnlyList<RemoteFolder>> GetFoldersAsync(string? parentId, CancellationToken cancellationToken);
}
public static class ProviderContract { public const int Version = 1; }

public sealed record ConnectionField(string Key, string Label, bool IsSecret = false);
/// <summary>Optional v1 extension for session-only configuration. Never persist secret values in manifests.</summary>
public interface IConfigurableProvider
{
    IReadOnlyList<ConnectionField> ConnectionFields { get; }
    bool IsConnected { get; }
    Task ConnectAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken);
}

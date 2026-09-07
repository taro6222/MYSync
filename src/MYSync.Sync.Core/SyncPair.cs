namespace MYSync.Sync.Core;
public sealed record SyncPair(Guid Id, string ProviderId, string LocalPath, string RemoteFolderId, string RemoteFolderName, bool Paused, Guid? AccountId = null);

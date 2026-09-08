namespace MYSync.Sync.Core;

/// <summary>A complete root-scoped endpoint. Failed or partial scans must include errors.</summary>
public interface ISyncEndpoint
{
    Task<ScanResult> ScanAsync(CancellationToken cancellationToken);
    /// <summary>Returns bytes for exactly the expected file version, or throws. Caller owns the stream.</summary>
    Task<Stream> OpenReadAsync(SyncEntry expected, CancellationToken cancellationToken);
    /// <summary>
    /// Verifies the target equals expected, verifies SHA256, and publishes the complete file.
    /// Null expected means create-only. Cancellation/failure must not expose partial content.
    /// Use atomic conditional writes where available. A local adapter may instead preserve the actual replaced version and block scans after an unverified replacement; it must never silently discard a late edit.
    /// </summary>
    Task PutFileAsync(string path, SyncEntry? expected, Stream content, string sha256, CancellationToken cancellationToken);
    /// <summary>Create-only. Rejects an existing item instead of silently accepting it.</summary>
    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken);
    /// <summary>Conditional removal of the expected file or empty directory. Local removal retains the original outside the root and blocks scans on unverified results. Never recursively destroys contents.</summary>
    Task DeleteAsync(SyncEntry expected, CancellationToken cancellationToken);
}
/// <summary>How a transfer failure should be treated. Only Transient may be retried without user action.</summary>
public enum SyncFailureKind { Unknown, Transient, Authentication, Permission, Missing, Precondition }
/// <summary>
/// A transfer failure carrying its classification. Providers should throw this instead of a bare IOException
/// so the engine never has to guess a retry policy from message text.
/// </summary>
public class SyncTransferException(string message, SyncFailureKind kind = SyncFailureKind.Unknown) : IOException(message)
{
    public SyncFailureKind Kind { get; } = kind;
}
/// <summary>The observed state did not match what the operation was planned against. Never retried blindly; the caller must re-scan.</summary>
public sealed class SyncPreconditionException(string message) : SyncTransferException(message, SyncFailureKind.Precondition);

/// <summary>Optional file-scoped revalidation. Must inspect current content; fresh server version metadata may validate a version-bound hash. A time-based planning cache alone is insufficient.
/// ConcurrentFiles opts into independent file operations only; directory changes remain exclusive.</summary>
public interface IFileStateEndpoint
{
    bool SupportsConcurrentFiles { get; }
    Task<SyncEntry?> InspectFileAsync(string path, CancellationToken cancellationToken);
}

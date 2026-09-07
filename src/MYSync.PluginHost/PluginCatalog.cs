using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using MYSync.Provider.Abstractions;
namespace MYSync.PluginHost;

public sealed record PluginManifest(string Id, string Version, int ContractVersion, string Assembly, string EntryType);
public sealed record PluginIssue(string Folder, string Message);
public sealed class PluginCatalog : IDisposable
{
    public List<IProvider> Providers { get; } = [];
    public List<PluginIssue> Issues { get; } = [];
    private readonly List<AssemblyLoadContext> contexts = [];
    public void Load(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var folder in Directory.GetDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
        {
            PluginContext? context = null;
            IProvider? provider = null;
            try
            {
                var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(Path.Combine(folder, "plugin.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("설명 파일이 비었습니다.");
                if (string.IsNullOrWhiteSpace(manifest.Id) || !Version.TryParse(manifest.Version, out _)) throw new InvalidDataException("ID 또는 버전이 올바르지 않습니다.");
                if (manifest.ContractVersion != ProviderContract.Version) throw new InvalidDataException("지원하지 않는 계약 버전입니다.");
                if (Providers.Any(p => p.Id == manifest.Id)) throw new InvalidDataException("중복 Provider ID입니다.");
                var assemblyPath = Path.GetFullPath(Path.Combine(folder, manifest.Assembly));
                if (!assemblyPath.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("플러그인 폴더 외부 경로입니다.");
                context = new PluginContext(assemblyPath);
                var type = context.LoadFromAssemblyPath(assemblyPath).GetType(manifest.EntryType, true)!;
                provider = Activator.CreateInstance(type) as IProvider ?? throw new InvalidDataException("Provider 계약을 구현하지 않았습니다.");
                if (provider.Id != manifest.Id) throw new InvalidDataException("Provider ID가 설명 파일과 다릅니다.");
                Providers.Add(provider);
                contexts.Add(context);
            }
            catch (Exception ex)
            {
                try { provider?.Dispose(); } catch { /* Keep original load failure. */ }
                context?.Unload();
                Issues.Add(new(folder, ex.GetBaseException().Message));
            }
        }
    }
    public void Dispose()
    {
        foreach (var provider in Providers) { try { provider.Dispose(); } catch { } }
        Providers.Clear();
        foreach (var context in contexts) context.Unload();
        contexts.Clear();
    }
    public IProvider CreateSession(string providerId)
    {
        var prototype = Providers.SingleOrDefault(x => x.Id == providerId) ?? throw new InvalidOperationException("Provider가 설치되지 않았습니다.");
        return Activator.CreateInstance(prototype.GetType()) as IProvider ?? throw new InvalidOperationException("Provider 세션을 만들 수 없습니다.");
    }
    private sealed class PluginContext(string path) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver resolver = new(path);
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == typeof(IProvider).Assembly.GetName().Name) return typeof(IProvider).Assembly;
            if (name.Name == typeof(MYSync.Sync.Core.ISyncEndpoint).Assembly.GetName().Name) return typeof(MYSync.Sync.Core.ISyncEndpoint).Assembly;
            var resolved = resolver.ResolveAssemblyToPath(name);
            return resolved is null ? null : LoadFromAssemblyPath(resolved);
        }
        protected override nint LoadUnmanagedDll(string name)
        {
            var resolved = resolver.ResolveUnmanagedDllToPath(name);
            return resolved is null ? 0 : LoadUnmanagedDllFromPath(resolved);
        }
    }
}

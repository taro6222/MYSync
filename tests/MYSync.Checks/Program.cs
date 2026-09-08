using MYSync.PluginHost;
using MYSync.Sync.Core;
using MYSync.Sync.Infrastructure;
using System.Text.Json;

var root = Path.GetFullPath(args[0]);
var scratch = Path.Combine(root, ".tools", "checks", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
SyncDiagnostics.DirectoryPath = Path.Combine(scratch, "logs");
var db = Path.Combine(scratch, "settings.db");
var pair = new SyncPair(Guid.NewGuid(), "missing-provider", @"D:\테스트 폴더", "remote-id", "원격 폴더", true);
new SettingsStore(db).Save(pair);
if (new SettingsStore(db).Load().Single() != pair) throw new Exception("Settings roundtrip failed");
var updated = pair with { Paused = false };
new SettingsStore(db).Save(updated);
if (new SettingsStore(db).Load().Single() != updated) throw new Exception("Settings update failed");
Console.WriteLine("PASS: SQLite persistence, update, missing Provider state preservation");
var plugins = Path.Combine(scratch, "plugins");
var source = Path.Combine(root, "src", "MYSync.Provider.Sample", "bin", "Debug", "net10.0");
foreach (var name in new[] { "01-valid", "02-duplicate", "03-incompatible", "04-traversal", "05-missing" })
{
    var folder = Path.Combine(plugins, name); Directory.CreateDirectory(folder);
    foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
    if (name == "03-incompatible") File.WriteAllText(Path.Combine(folder,"plugin.json"), JsonSerializer.Serialize(new PluginManifest("incompatible","1.0.0",99,"MYSync.Provider.Sample.dll","MYSync.Provider.Sample.SampleProvider")));
    if (name == "04-traversal") File.WriteAllText(Path.Combine(folder,"plugin.json"), JsonSerializer.Serialize(new PluginManifest("traversal","1.0.0",1,"../escape.dll","X")));
    if (name == "05-missing") File.WriteAllText(Path.Combine(folder,"plugin.json"), JsonSerializer.Serialize(new PluginManifest("missing","1.0.0",1,"missing.dll","X")));
}
using var catalog = new PluginCatalog(); catalog.Load(plugins);
if (catalog.Providers.Count != 1 || catalog.Issues.Count != 4) throw new Exception("Plugin validation failed");
var folders = await catalog.Providers.Single().GetFoldersAsync(null, CancellationToken.None);
if (folders.Single().Id != "sample-root") throw new Exception("Shared contract invocation failed");
Console.WriteLine("PASS: DLL discovery, shared contract invocation, duplicate/incompatible/path escape/missing assembly rejection");
await EngineChecks.Run(scratch);
await ExecutionChecks.Run(scratch);
await LocalEndpointChecks.Run(scratch);
await WebDavChecks.Run(root);
AccountChecks.Run(scratch);
await WebDavTransferChecks.Run(scratch);
await MonitorChecks.Run(scratch);
PreferencesChecks.Run(scratch);
await RecoveryChecks.Run(scratch);
await ManagementChecks.Run(scratch);
await ResilienceChecks.Run(scratch);
await UnsupportedChecks.Run(scratch);
await DiagnosticChecks.Run(scratch);
await ScanCacheChecks.Run(scratch);
Console.WriteLine("All checks passed.");

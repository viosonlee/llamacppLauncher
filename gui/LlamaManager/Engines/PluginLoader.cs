using System.IO;
using System.Text.Json;
using LlamaManager.Services;

namespace LlamaManager.Engines;

/// <summary>
/// Discovers plugin manifests under the manager root (<c>plugins/&lt;id&gt;/plugin.json</c>) and
/// under the install's engines folder (<c>engines/&lt;id&gt;/plugin.json</c>). Invalid manifests
/// are skipped with a recorded reason, never a crash.
/// </summary>
public static class PluginLoader
{
    public sealed record Loaded(ProcessPluginBackend Backend, string ManifestPath);
    public sealed record Rejected(string ManifestPath, string Reason);

    public static (IReadOnlyList<Loaded> Loaded, IReadOnlyList<Rejected> Rejected) Discover()
    {
        var loaded = new List<Loaded>();
        var rejected = new List<Rejected>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in SearchDirectories())
        {
            if (!Directory.Exists(directory)) continue;
            foreach (var manifestPath in SafeEnumerate(directory))
            {
                if (!seen.Add(Path.GetFullPath(manifestPath))) continue;
                try
                {
                    var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath));
                    if (manifest == null) { rejected.Add(new Rejected(manifestPath, "清单为空。")); continue; }
                    var problem = manifest.Validate();
                    if (problem != null) { rejected.Add(new Rejected(manifestPath, problem)); continue; }
                    if (manifest.ExecutableForCurrentOs() == null)
                    {
                        rejected.Add(new Rejected(manifestPath, "清单没有当前系统的 executable。"));
                        continue;
                    }
                    if (BackendRegistry.ById(manifest.Id) != null)
                    {
                        rejected.Add(new Rejected(manifestPath, $"id {manifest.Id} 已被内置后端占用。"));
                        continue;
                    }
                    loaded.Add(new Loaded(new ProcessPluginBackend(manifest, Path.GetDirectoryName(manifestPath)!).Initialize(), manifestPath));
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    rejected.Add(new Rejected(manifestPath, "解析失败：" + ex.Message));
                }
            }
        }
        return (loaded, rejected);
    }

    /// <summary>Registers every valid manifest backend. Returns the rejections for display.</summary>
    public static IReadOnlyList<Rejected> RegisterAll()
    {
        var (loaded, rejected) = Discover();
        foreach (var item in loaded) BackendRegistry.Register(item.Backend);
        return rejected;
    }

    private static IEnumerable<string> SearchDirectories()
    {
        // Bundled manifests ship beside the launcher; user manifests live under the manager root
        // (or an installed engine folder). A plugin's own folder holds its executable.
        yield return Path.Combine(AppContext.BaseDirectory, "plugins");
        yield return Path.Combine(ConfigService.GetManagerRoot(), "plugins");
        yield return Path.Combine(ConfigService.GetLlamaCppRoot(), "engines");
    }

    private static IEnumerable<string> SafeEnumerate(string directory)
    {
        IEnumerable<string> manifests;
        try { manifests = Directory.EnumerateFiles(directory, "plugin.json", SearchOption.AllDirectories); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var manifest in manifests)
        {
            // plugins/<id>/plugin.json (user) or engines/<id>/plugin.json (install)
            yield return manifest;
        }
    }
}

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LlamaManager.Models;

namespace LlamaManager.Services;

/// <summary>Serialized read/modify/write transactions, atomic replacement and recoverable backups.</summary>
public sealed class ConfigStore(string path)
{
    private readonly string _path = Path.GetFullPath(path);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public string? RecoveryWarning { get; private set; }

    private Mutex CreateMutex() => new(false, "Local\\LlamaManager.Config." +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_path.ToUpperInvariant())))[..24]);

    private T Locked<T>(Func<T> action)
    {
        using var mutex = CreateMutex();
        var held = false;
        try
        {
            try { held = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { held = true; }
            if (!held) throw new IOException("配置正被其他进程使用，请稍后再试。");
            return action();
        }
        finally { if (held) mutex.ReleaseMutex(); }
    }

    public ManagerConfig Read() => Locked(ReadCore);

    private static ManagerConfig ReadFile(string path)
    {
        var config = JsonSerializer.Deserialize<ManagerConfig>(File.ReadAllText(path))
            ?? throw new InvalidDataException("配置内容为空。");
        config.ModelPresets ??= [];
        if (config.ModelPath == null || config.Args == null || config.ModelsDirectory == null ||
            config.ModelPresets.Any(p => p.Value == null || p.Value.Args == null) ||
            config.Port is < 1 or > 65535 || config.EmbeddingPort is < 1 or > 65535)
            throw new InvalidDataException("配置字段无效。");
        return config;
    }

    private ManagerConfig ReadCore()
    {
        RecoveryWarning = null;
        if (!File.Exists(_path)) return new ManagerConfig();
        try { return ReadFile(_path); }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            try
            {
                var recovered = ReadFile(_path + ".bak");
                RecoveryWarning = "配置损坏，已读取上次备份。原文件会保留为 .corrupt 文件。";
                return recovered;
            }
            catch (Exception backupError) when (backupError is IOException or InvalidDataException or JsonException)
            {
                throw new InvalidDataException($"无法读取配置，且没有有效备份。已保留原文件：{_path}", error);
            }
        }
    }

    public ManagerConfig Update(Action<ManagerConfig> update) => Locked(() =>
    {
        var config = ReadCore();
        update(config);
        WriteCore(config);
        return config;
    });

    public void Save(ManagerConfig config) => Locked(() => { ReadCore(); WriteCore(config); return true; });

    private void WriteCore(ManagerConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(config, JsonOptions));
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(_path))
            {
                if (RecoveryWarning != null)
                {
                    File.Copy(_path, _path + "." + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".corrupt");
                    File.Replace(temporary, _path, null);
                }
                else File.Replace(temporary, _path, _path + ".bak");
            }
            else File.Move(temporary, _path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

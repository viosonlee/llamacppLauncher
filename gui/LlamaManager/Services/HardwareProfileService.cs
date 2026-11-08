using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LlamaManager.Services;

public sealed record GpuHardwareInfo
{
    public int Index { get; init; }
    public string? Uuid { get; init; }
    public string? Name { get; init; }
    public long? TotalMemoryBytes { get; init; }
    public long? FreeMemoryBytes { get; init; }
    public long? UsedMemoryBytes { get; init; }
    public double? ComputeCapability { get; init; }
}

public sealed record DiskHardwareInfo
{
    public string CachePath { get; init; } = "";
    public string? VolumeRoot { get; init; }
    public long? TotalBytes { get; init; }
    public long? FreeBytes { get; init; }
    public string? MediaType { get; init; }
}

/// <summary>Serializable observations; null means unknown, never a fabricated zero.</summary>
public sealed record HardwareSnapshot
{
    public DateTimeOffset CapturedAtUtc { get; init; }
    public string? CpuName { get; init; }
    public int? PhysicalCores { get; init; }
    public int? LogicalProcessors { get; init; }
    public long? TotalRamBytes { get; init; }
    public long? AvailableRamBytes { get; init; }
    public IReadOnlyList<GpuHardwareInfo> Gpus { get; init; } = Array.Empty<GpuHardwareInfo>();
    public DiskHardwareInfo? CacheDisk { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    // Deliberately excludes timestamp, free/used RAM/VRAM/disk, warnings and cache location.
    public string Fingerprint => StableHash(JsonSerializer.Serialize(new
    {
        Cpu = CpuName?.Trim().ToUpperInvariant(), PhysicalCores, LogicalProcessors, TotalRamBytes,
        Gpus = Gpus.OrderBy(g => g.Index).ThenBy(g => g.Uuid, StringComparer.Ordinal).Select(g => new
        {
            g.Index, Uuid = g.Uuid?.Trim().ToUpperInvariant(), Name = g.Name?.Trim().ToUpperInvariant(),
            g.TotalMemoryBytes, g.ComputeCapability
        })
    }));

    internal static string StableHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

/// <summary>
/// Read-only, off-dispatcher, bounded device observations. No services, downloads or configuration writes.
/// The injected query receives the absolute cache path, and must not mutate the returned snapshot.
/// </summary>
public sealed class HardwareProfileService
{
    private readonly Func<string, CancellationToken, Task<HardwareSnapshot>> _query;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _ttl;
    private readonly TimeSpan _queryTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HardwareSnapshot? _cached;
    private string? _cachedPath;
    private DateTimeOffset _expires;

    public HardwareProfileService(Func<string, CancellationToken, Task<HardwareSnapshot>>? query = null,
        TimeSpan? ttl = null, Func<DateTimeOffset>? utcNow = null, TimeSpan? queryTimeout = null)
    {
        _query = query ?? QuerySystemAsync;
        _ttl = ttl ?? TimeSpan.FromSeconds(30);
        _queryTimeout = queryTimeout ?? TimeSpan.FromSeconds(12);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        if (_ttl < TimeSpan.Zero || _queryTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl), "TTL must be nonnegative and query timeout positive.");
    }

    public static string DefaultCachePath(string managerRoot) =>
        Path.GetFullPath(Path.Combine(managerRoot, "data", "kvmem-cache"));

    public Task<HardwareSnapshot> ProbeAsync(string managerRoot, CancellationToken ct, bool forceRefresh = false) =>
        ProbeCachePathAsync(DefaultCachePath(managerRoot), ct, forceRefresh);

    public async Task<HardwareSnapshot> ProbeCachePathAsync(string cachePath, CancellationToken ct, bool forceRefresh = false)
    {
        ct.ThrowIfCancellationRequested();
        var path = Path.GetFullPath(cachePath);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _cached != null && _utcNow() < _expires &&
                string.Equals(path, _cachedPath, StringComparison.OrdinalIgnoreCase)) return _cached;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(_queryTimeout);
            HardwareSnapshot snapshot;
            try
            {
                // Even synchronous portions of injected/default queries never run on a UI dispatcher.
                snapshot = await Task.Run(() => _query(path, deadline.Token), deadline.Token)
                    .WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                snapshot = new HardwareSnapshot { Warnings = ["硬件探测超时；数据未知，不能据此认定无硬件。"] };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                snapshot = new HardwareSnapshot { Warnings = ["硬件探测失败：" + ex.Message] };
            }
            ct.ThrowIfCancellationRequested();
            _cached = snapshot with
            {
                CapturedAtUtc = _utcNow(),
                Gpus = Array.AsReadOnly(snapshot.Gpus.ToArray()),
                Warnings = Array.AsReadOnly(snapshot.Warnings.ToArray())
            };
            _cachedPath = path;
            _expires = _utcNow() + _ttl;
            return _cached;
        }
        finally { _gate.Release(); }
    }

    private static async Task<HardwareSnapshot> QuerySystemAsync(string cachePath, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return new HardwareSnapshot { Warnings = ["仅实现 Windows 硬件探测；CPU/RAM/GPU/磁盘未知。"] };
        var cpuTask = QueryCpuRamAsync(ct);
        var gpuTask = QueryGpusAsync(ct);
        var diskTask = Task.Run(() => QueryDisk(cachePath), ct);
        await Task.WhenAll(cpuTask, gpuTask, diskTask).ConfigureAwait(false);
        var (cpu, cpuWarnings) = await cpuTask.ConfigureAwait(false);
        var (gpus, gpuWarnings) = await gpuTask.ConfigureAwait(false);
        var (disk, diskWarnings) = await diskTask.ConfigureAwait(false);
        return cpu with { Gpus = gpus, CacheDisk = disk, Warnings = cpuWarnings.Concat(gpuWarnings).Concat(diskWarnings).ToArray() };
    }

    private static async Task<(HardwareSnapshot Snapshot, string[] Warnings)> QueryCpuRamAsync(CancellationToken ct)
    {
        // A child process bounds WMI itself, not just our wait on an uninterruptible WMI worker.
        const string script = "[Console]::OutputEncoding=[Text.Encoding]::UTF8;$ErrorActionPreference='Stop';" +
            "$cpu=@(Get-CimInstance Win32_Processor -OperationTimeoutSec 3 | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors);" +
            "$os=Get-CimInstance Win32_OperatingSystem -OperationTimeoutSec 3;" +
            "@{Cpu=$cpu;TotalKiB=$os.TotalVisibleMemorySize;FreeKiB=$os.FreePhysicalMemory}|ConvertTo-Json -Depth 4 -Compress";
        try
        {
            var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            var output = await CaptureAsync(executable,
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))], ct)
                .ConfigureAwait(false);
            using var json = JsonDocument.Parse(output);
            var root = json.RootElement;
            var cpus = root.GetProperty("Cpu").EnumerateArray().ToArray();
            int? Sum(string key)
            {
                var values = cpus.Select(c => JsonPositive(c, key)).ToArray();
                return values.Length > 0 && values.All(v => v is > 0) && values.Sum(v => v!.Value) <= int.MaxValue
                    ? (int)values.Sum(v => v!.Value) : null;
            }
            var warnings = new List<string>();
            var result = new HardwareSnapshot
            {
                CpuName = string.Join(" / ", cpus.Select(c => c.GetProperty("Name").GetString()?.Trim()).Where(n => !string.IsNullOrEmpty(n))),
                PhysicalCores = Sum("NumberOfCores"), LogicalProcessors = Sum("NumberOfLogicalProcessors"),
                TotalRamBytes = KiB(JsonPositive(root, "TotalKiB")),
                AvailableRamBytes = KiB(JsonNonnegative(root, "FreeKiB"))
            };
            if (string.IsNullOrWhiteSpace(result.CpuName)) result = result with { CpuName = null };
            if (result.CpuName == null || result.PhysicalCores == null || result.LogicalProcessors == null)
                warnings.Add("WMI CPU 名称或物理/逻辑核心数未知；不以逻辑线程冒充物理核心。");
            if (result.TotalRamBytes == null || result.AvailableRamBytes == null) warnings.Add("WMI 总/可用 RAM 数据不完整。");
            return (result, warnings.ToArray());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return (new HardwareSnapshot(), ["CPU/RAM WMI 探测失败：" + ex.Message]); }
    }

    private static async Task<(IReadOnlyList<GpuHardwareInfo> Gpus, string[] Warnings)> QueryGpusAsync(CancellationToken ct)
    {
        try
        {
            var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
            if (!File.Exists(executable)) executable = "nvidia-smi.exe";
            var output = await CaptureAsync(executable,
                ["--query-gpu=index,uuid,name,memory.total,memory.free,memory.used,compute_cap", "--format=csv,noheader,nounits"], ct)
                .ConfigureAwait(false);
            var warnings = new List<string>();
            var gpus = ParseNvidiaSmi(output, warnings);
            if (gpus.Count == 0) warnings.Add("未发现 NVIDIA GPU，或 nvidia-smi 未返回可识别设备；不支持自动推荐。");
            return (gpus, warnings.ToArray());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return (Array.Empty<GpuHardwareInfo>(), ["NVIDIA GPU 探测失败（并非确认无 GPU）：" + ex.Message]); }
    }

    /// <summary>Pure parser exposed for no-GPU regression fixtures; unknown numeric fields remain null.</summary>
    public static IReadOnlyList<GpuHardwareInfo> ParseNvidiaSmi(string output, ICollection<string> warnings)
    {
        var result = new List<GpuHardwareInfo>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            // Numeric columns are anchored at both ends; a comma in a GPU name remains part of its name.
            var cells = line.Split(',').Select(s => s.Trim().Trim('"')).ToArray();
            if (cells.Length < 7 || !int.TryParse(cells[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) || index < 0)
            { warnings.Add("忽略无法解析的 nvidia-smi GPU 行。"); continue; }
            var end = cells.Length;
            var gpu = new GpuHardwareInfo
            {
                Index = index, Uuid = Known(cells[1]), Name = Known(string.Join(", ", cells.Skip(2).Take(end - 6))),
                TotalMemoryBytes = MiB(cells[end - 4], positive: true), FreeMemoryBytes = MiB(cells[end - 3]),
                UsedMemoryBytes = MiB(cells[end - 2]),
                ComputeCapability = double.TryParse(cells[end - 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var cc) &&
                    double.IsFinite(cc) && cc > 0 ? cc : null
            };
            if (gpu.Uuid == null || gpu.Name == null || gpu.TotalMemoryBytes == null || gpu.FreeMemoryBytes == null ||
                gpu.UsedMemoryBytes == null || gpu.ComputeCapability == null)
                warnings.Add($"GPU {index} 部分数据未知；不会将 N/A 当作零。");
            result.Add(gpu);
        }
        return result;
    }

    private static string? Known(string text) => string.IsNullOrWhiteSpace(text) ||
        text.Contains("N/A", StringComparison.OrdinalIgnoreCase) || text.StartsWith('[') ? null : text;
    private static long? MiB(string text, bool positive = false) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) &&
        value >= (positive ? 1 : 0) && value <= long.MaxValue / (1L << 20) ? value * (1L << 20) : null;
    private static long? JsonNonnegative(JsonElement value, string key) => value.TryGetProperty(key, out var item) &&
        long.TryParse(item.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 0 ? n : null;
    private static long? JsonPositive(JsonElement value, string key) => JsonNonnegative(value, key) is > 0 and var n ? n : null;
    private static long? KiB(long? value) => value is >= 0 and <= long.MaxValue / 1024 ? value * 1024 : null;

    private static async Task<string> CaptureAsync(string fileName, string[] args, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        var start = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!process.Start()) throw new IOException("无法启动只读硬件探测进程。");
            // Terminate on the deadline independently of redirected-pipe cancellation behavior.
            using var stopOnCancellation = deadline.Token.Register(() => KillProbe(process));
            var stdout = ReadBoundedAsync(process.StandardOutput, deadline.Token);
            var stderr = ReadBoundedAsync(process.StandardError, deadline.Token);
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(deadline.Token)).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException($"探测进程退出 {process.ExitCode}：{await stderr.ConfigureAwait(false)}");
            return await stdout.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("只读硬件探测进程超过 8 秒。"); }
        finally { KillProbe(process); }
    }

    private static void KillProbe(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        catch (AggregateException) { } // Child already exited while enumerating its process tree.
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        var buffer = new char[2048];
        var result = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) != 0)
        {
            if (result.Length + read > 65536) throw new IOException("硬件探测输出超过 64 KiB 限制。");
            result.Append(buffer, 0, read);
        }
        return result.ToString();
    }

    private static (DiskHardwareInfo Disk, string[] Warnings) QueryDisk(string cachePath)
    {
        var disk = new DiskHardwareInfo { CachePath = cachePath };
        var warnings = new List<string> { "缓存盘介质类型未知；未确认 SSD/NVMe，不承诺磁盘分页性能。" };
        try
        {
            var root = Path.GetPathRoot(cachePath);
            if (string.IsNullOrEmpty(root) || root.StartsWith("\\\\", StringComparison.Ordinal) ||
                new DriveInfo(root).DriveType != DriveType.Fixed)
                throw new IOException("缓存路径不是可确认的本机固定磁盘。");
            // No directory creation. Query the nearest existing ancestor; native API honors mounted volumes.
            var existing = cachePath;
            while (!Directory.Exists(existing))
                existing = Path.GetDirectoryName(existing) ?? throw new DirectoryNotFoundException("缓存路径没有可访问父目录。");
            var volume = new StringBuilder(1024);
            if (!GetVolumePathName(existing, volume, volume.Capacity) ||
                !GetDiskFreeSpaceEx(existing, out var available, out var total, out _))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            disk = disk with { VolumeRoot = volume.ToString(), TotalBytes = CheckedBytes(total), FreeBytes = CheckedBytes(available) };
            if (disk.TotalBytes == null || disk.FreeBytes == null) warnings.Add("缓存盘容量超出可表示范围，容量未知。");
        }
        catch (Exception ex) { warnings.Add("缓存盘空闲容量未知：" + ex.Message); }
        return (disk, warnings.ToArray());
    }

    private static long? CheckedBytes(ulong value) => value <= long.MaxValue ? (long)value : null;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out ulong available, out ulong total, out ulong free);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(string path, StringBuilder volumePath, int length);
}

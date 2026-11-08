using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LlamaManager.Services;

/// <summary>Query the kernel's process image before the loader's module list is ready.</summary>
public static class ProcessIdentityService
{
    public static string? ExecutablePath(Process process)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return process.MainModule?.FileName;
            using var handle = OpenProcess(0x1000, false, process.Id);
            if (handle.IsInvalid) return null;
            var buffer = new StringBuilder(32768);
            uint length = (uint)buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref length) ? buffer.ToString() : null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException) { return null; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder fileName, ref uint size);
}

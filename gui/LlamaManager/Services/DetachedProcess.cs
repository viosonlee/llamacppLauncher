using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LlamaManager.Services;

/// <summary>Redirects directly to files so the server survives GUI/CLI exit without broken pipes.</summary>
internal static class DetachedProcess
{
    public static Process Start(string executable, string arguments, string stdout, string stderr)
    {
        var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
        using var output = CreateFile(stdout, 0x40000000, 3, ref security, 2, 0x80, IntPtr.Zero);
        using var error = CreateFile(stderr, 0x40000000, 3, ref security, 2, 0x80, IntPtr.Zero);
        using var input = CreateFile("NUL", 0x80000000, 3, ref security, 3, 0x80, IntPtr.Zero);
        if (output.IsInvalid || error.IsInvalid || input.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var startup = new StartupInfoEx
        {
            Startup = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100,
                StdOutput = output.DangerousGetHandle(), StdError = error.DangerousGetHandle(), StdInput = input.DangerousGetHandle()
            }
        };
        var command = new StringBuilder(LauncherArguments.Quote(executable) + " " + arguments);
        // Whitelist only these three handles. Inheriting the CLI parent's pipes would keep
        // its caller waiting for EOF until the model server exits.
        nuint size = 0;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        startup.Attributes = Marshal.AllocHGlobal((nint)size);
        var handles = Marshal.AllocHGlobal(3 * IntPtr.Size);
        var initialized = false;
        try
        {
            initialized = InitializeProcThreadAttributeList(startup.Attributes, 1, 0, ref size);
            if (!initialized) throw new Win32Exception(Marshal.GetLastWin32Error());
            Marshal.Copy(new[] { startup.Startup.StdInput, startup.Startup.StdOutput, startup.Startup.StdError }, 0, handles, 3);
            if (!UpdateProcThreadAttribute(startup.Attributes, 0, (nuint)0x20002, handles, (nuint)(3 * IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, true, 0x08080000,
                IntPtr.Zero, Path.GetDirectoryName(executable), ref startup, out var process))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try { return Process.GetProcessById((int)process.Id); }
            finally { CloseHandle(process.Process); CloseHandle(process.Thread); }
        }
        finally
        {
            if (initialized) DeleteProcThreadAttributeList(startup.Attributes);
            Marshal.FreeHGlobal(startup.Attributes);
            Marshal.FreeHGlobal(handles);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, ReservedSize;
        public IntPtr ReservedPointer, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process, Thread; public uint Id, ThreadId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { public StartupInfo Startup; public IntPtr Attributes; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint sharing,
        ref SecurityAttributes security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string? directory,
        ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}

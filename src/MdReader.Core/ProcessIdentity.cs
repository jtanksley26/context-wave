using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MdReader.Core;

/// <summary>
/// Names a process as "pid@start-time", so a chat's process can be recognised from both its tool
/// bridge (a direct child) and its reply hook (a child, or a grandchild under Git Bash), and a
/// process id that Windows hands out again later is not mistaken for the old one.
/// </summary>
public static class ProcessIdentity
{
    private const int MaxDepth = 8;

    /// <summary>The key for a process, or its bare id when its start time cannot be read.</summary>
    public static string KeyOf(int pid) =>
        StartTime(pid) is { } start ? $"{pid}@{start.Ticks}" : pid.ToString();

    /// <summary>The key of this process's parent, or null when it cannot be found.</summary>
    public static string? ParentKey()
    {
        var parents = ParentMap();
        return parents.TryGetValue(Environment.ProcessId, out var parent) && IsParent(parent, Environment.ProcessId)
            ? KeyOf(parent)
            : null;
    }

    /// <summary>The keys of the processes above this one, nearest first.</summary>
    public static IReadOnlyList<string> AncestorKeys()
    {
        var parents = ParentMap();
        var keys = new List<string>();
        var seen = new HashSet<int> { Environment.ProcessId };
        var child = Environment.ProcessId;
        while (keys.Count < MaxDepth && parents.TryGetValue(child, out var parent) && seen.Add(parent)
               && IsParent(parent, child))
        {
            keys.Add(KeyOf(parent));
            child = parent;
        }
        return keys;
    }

    /// <summary>
    /// A recorded parent can have exited and its id been reused by a newer process; a real parent
    /// started before its child.
    /// </summary>
    private static bool IsParent(int parent, int child) =>
        parent > 0 && (StartTime(parent) is not { } p || StartTime(child) is not { } c || p <= c);

    private static DateTime? StartTime(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Every process's parent id, from one snapshot of the process list.</summary>
    private static Dictionary<int, int> ParentMap()
    {
        var map = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == InvalidHandle) return map;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
                map[(int)entry.ProcessId] = (int)entry.ParentProcessId;
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return map;
    }

    private const uint SnapProcess = 0x00000002;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}

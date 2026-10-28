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
        var processes = ProcessMap();
        return processes.TryGetValue(Environment.ProcessId, out var entry) && IsParent(entry.Parent, Environment.ProcessId)
            ? KeyOf(entry.Parent)
            : null;
    }

    /// <summary>
    /// For the reply hook: the key of the chat's process (the parent of its tool bridge), or null.
    /// Processes further up are shared by other chats, so they would route other chats' calls here.
    /// </summary>
    public static string? ChatKey()
    {
        var processes = ProcessMap();
        var self = Environment.ProcessId;
        var ancestors = new List<int>();
        var child = self;
        while (ancestors.Count < MaxDepth && processes.TryGetValue(child, out var entry) && IsParent(entry.Parent, child))
        {
            ancestors.Add(entry.Parent);
            child = entry.Parent;
        }
        var exe = Path.GetFileName(Environment.ProcessPath) ?? "";
        return ChatProcess(ancestors, processes, self, exe) is { } chat ? KeyOf(chat) : null;
    }

    // What Claude Code may run a hook through, between the chat and the hook.
    private static readonly HashSet<string> Shells = new(StringComparer.OrdinalIgnoreCase)
    {
        "bash.exe", "sh.exe", "cmd.exe", "pwsh.exe", "powershell.exe",
    };

    /// <summary>
    /// The chat: the first ancestor that is not a shell, provided it has started a tool bridge (a
    /// copy of this program other than this one; the hook runs as a copy too). Never one further
    /// up: when the chat has no bridge, the next process up is shared by other chats.
    /// </summary>
    /// <param name="ancestors">Process ids above <paramref name="self"/>, nearest first.</param>
    /// <param name="processes">Every process's parent id and program file name.</param>
    public static int? ChatProcess(
        IReadOnlyList<int> ancestors, IReadOnlyDictionary<int, (int Parent, string Exe)> processes, int self,
        string exe)
    {
        foreach (var ancestor in ancestors)
        {
            if (processes.Any(p => p.Key != self && p.Value.Parent == ancestor
                                   && string.Equals(p.Value.Exe, exe, StringComparison.OrdinalIgnoreCase)))
                return ancestor;
            if (!processes.TryGetValue(ancestor, out var entry) || !Shells.Contains(entry.Exe)) return null;
        }
        return null;
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

    /// <summary>Every process's parent id and program file name, from one snapshot of the process list.</summary>
    private static Dictionary<int, (int Parent, string Exe)> ProcessMap()
    {
        var map = new Dictionary<int, (int Parent, string Exe)>();
        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == InvalidHandle) return map;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (var more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
                map[(int)entry.ProcessId] = ((int)entry.ParentProcessId, entry.ExeFile ?? "");
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

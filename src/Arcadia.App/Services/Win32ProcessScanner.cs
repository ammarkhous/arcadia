using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Arcadia.Core.Tracking;

namespace Arcadia.App.Services;

/// <summary>
/// Lists processes with their full image path and start time using PROCESS_QUERY_LIMITED_INFORMATION,
/// which works without elevation (including for most elevated/anti-cheat protected games).
/// Paths are cached per (pid, start time) so repeated scans stay cheap.
/// </summary>
public sealed class Win32ProcessScanner : IProcessScanner
{
    private readonly Dictionary<int, (long CreationTime, string Path)> _cache = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<RunningProcess> Snapshot()
    {
        var result = new List<RunningProcess>();
        var seen = new HashSet<int>();
        foreach (var pid in EnumerateProcessIds())
        {
            if (pid <= 4) continue;
            if (TryDescribe(pid) is { } p) { result.Add(p); seen.Add(pid); }
        }
        lock (_gate)
        {
            foreach (var stale in _cache.Keys.Where(k => !seen.Contains(k)).ToList()) _cache.Remove(stale);
        }
        return result;
    }

    public IReadOnlyList<RunningProcess> WindowedSnapshot()
    {
        var pids = new HashSet<int>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (Native.IsWindowVisible(hwnd) && Native.GetWindow(hwnd, Native.GW_OWNER) == IntPtr.Zero && Native.GetWindowTextLength(hwnd) > 0)
            {
                Native.GetWindowThreadProcessId(hwnd, out var pid);
                pids.Add((int)pid);
            }
            return true;
        }, IntPtr.Zero);
        return pids.Select(TryDescribe).Where(p => p != null).ToList()!;
    }

    private RunningProcess? TryDescribe(int pid)
    {
        var handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            if (!Native.GetProcessTimes(handle, out var creation, out _, out _, out _)) return null;
            string? path;
            lock (_gate)
            {
                path = _cache.TryGetValue(pid, out var cached) && cached.CreationTime == creation ? cached.Path : null;
            }
            if (path == null)
            {
                var buffer = new StringBuilder(1024);
                int size = buffer.Capacity;
                if (!Native.QueryFullProcessImageName(handle, 0, buffer, ref size)) return null;
                path = buffer.ToString();
                lock (_gate) _cache[pid] = (creation, path);
            }
            return new RunningProcess(pid, path, DateTime.FromFileTimeUtc(creation));
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    private static IEnumerable<int> EnumerateProcessIds()
    {
        var ids = new int[2048];
        while (true)
        {
            if (!Native.EnumProcesses(ids, ids.Length * sizeof(int), out var bytes)) return Process.GetProcesses().Select(p => { using (p) return p.Id; }).ToArray();
            if (bytes < ids.Length * sizeof(int)) return ids.Take(bytes / sizeof(int)).ToArray();
            ids = new int[ids.Length * 2];
        }
    }

    private static class Native
    {
        public const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public const uint GW_OWNER = 4;

        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("psapi.dll", SetLastError = true)]
        public static extern bool EnumProcesses([Out] int[] processIds, int size, out int bytesReturned);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(int access, bool inherit, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextLength(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    }
}

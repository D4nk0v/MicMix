using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MicMix.Apps;

/// <summary>A program as the user thinks of it: all processes of one exe, e.g. every chrome.exe.</summary>
public sealed record AudioApp(
    string Key,                 // exe name without extension, lower-case: "chrome"
    string DisplayName,
    string? ExePath,
    IReadOnlyCollection<int> RootProcessIds,
    bool HasAudioSession,
    bool IsPlaying);

/// <summary>
/// Finds programs that use audio or have a window, and resolves each to the root processes worth capturing.
/// Any of them can go into the mic: process-loopback capture simply stays silent until the app plays something.
/// </summary>
public static class AppScanner
{
    /// <summary>Never offered, never captured: the system audio engine, and ourselves (instant feedback loop).</summary>
    public static readonly HashSet<string> NeverCapture = new(StringComparer.OrdinalIgnoreCase)
    {
        "audiodg", "micmix",
    };

    /// <summary>
    /// Allowed, but with a warning: in the mic they'd send friends' own voices back to them as an echo
    /// (the exact bug this app was written to avoid). Useful only for e.g. relaying another call.
    /// </summary>
    public static readonly HashSet<string> VoiceChats = new(StringComparer.OrdinalIgnoreCase)
    {
        "discord", "discordptb", "discordcanary", "discorddevelopment",
        "teamspeak", "ts3client_win64", "mumble", "skype", "ms-teams", "zoom", "telegram",
    };

    /// <summary>Windowed system shells and hosts that never make sound of their own; listing them is just noise.</summary>
    private static readonly HashSet<string> WindowNoise = new(StringComparer.OrdinalIgnoreCase)
    {
        "applicationframehost", "textinputhost", "shellexperiencehost", "startmenuexperiencehost", "searchhost",
        "lockapp", "systemsettings", "explorer", "taskmgr", "nvidia overlay", "widgets", "dwm",
    };

    private static readonly Dictionary<string, string> DisplayNameCache = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="alsoInclude">Keys the user selected; included even if they currently have no audio session.</param>
    public static List<AudioApp> Scan(IEnumerable<string> alsoInclude)
    {
        var processes = SnapshotProcesses();
        var sessionState = new Dictionary<int, bool>(); // pid -> is playing
        int self = Environment.ProcessId;

        using (var enumerator = new MMDeviceEnumerator())
        {
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    var manager = device.AudioSessionManager;
                    manager.RefreshSessions();
                    var sessions = manager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        var s = sessions[i];
                        if (s.IsSystemSoundsSession || s.State == AudioSessionState.AudioSessionStateExpired) continue;
                        int pid = (int)s.GetProcessID;
                        if (pid == 0 || pid == self) continue;
                        bool playing = s.State == AudioSessionState.AudioSessionStateActive;
                        sessionState[pid] = sessionState.GetValueOrDefault(pid) || playing;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Session scan of {device.FriendlyName} failed: {ex.Message}");
                }
                finally
                {
                    device.Dispose();
                }
            }
        }

        var wanted = new HashSet<string>(alsoInclude, StringComparer.OrdinalIgnoreCase);
        foreach (var pid in sessionState.Keys)
            if (processes.TryGetValue(pid, out var p)) wanted.Add(p.Key);
        // Programs that haven't played anything yet still count: pick a game or player before it starts.
        foreach (var pid in WindowedProcessIds())
            if (processes.TryGetValue(pid, out var p) && !WindowNoise.Contains(p.Key)) wanted.Add(p.Key);
        wanted.ExceptWith(NeverCapture);

        var result = new List<AudioApp>();
        foreach (var key in wanted)
        {
            // Root = a process of this exe whose parent is not the same exe. Capturing the root's
            // process tree picks up audio from helper children (Chrome's audio service, etc.).
            var roots = processes.Values
                .Where(p => p.Key == key && !(processes.TryGetValue(p.ParentPid, out var parent) && parent.Key == key))
                .Select(p => p.Pid)
                .ToList();

            var members = processes.Values.Where(p => p.Key == key).Select(p => p.Pid).ToList();
            bool hasSession = members.Any(sessionState.ContainsKey);
            bool playing = members.Any(pid => sessionState.GetValueOrDefault(pid));

            string? path = roots.Select(GetExePath).FirstOrDefault(p => p != null);
            result.Add(new AudioApp(key, GetDisplayName(key, path), path, roots, hasSession, playing));
        }
        return result;
    }

    private static string GetDisplayName(string key, string? path)
    {
        if (DisplayNameCache.TryGetValue(key, out var cached)) return cached;
        string name = key;
        if (path != null)
        {
            try
            {
                var desc = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(desc)) name = desc.Trim();
            }
            catch { /* keep exe name */ }
            DisplayNameCache[key] = name;
        }
        return name;
    }

    #region Win32

    /// <summary>Processes owning a visible, titled top-level window (what the user sees in Alt+Tab, roughly).</summary>
    private static HashSet<int> WindowedProcessIds()
    {
        var pids = new HashSet<int>();
        EnumWindows((hwnd, _) =>
        {
            if (IsWindowVisible(hwnd) && GetWindowTextLengthW(hwnd) > 0 && GetWindow(hwnd, 4 /* GW_OWNER */) == IntPtr.Zero)
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                pids.Add((int)pid);
            }
            return true;
        }, IntPtr.Zero);
        return pids;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    private sealed record ProcInfo(int Pid, int ParentPid, string Key);

    private static Dictionary<int, ProcInfo> SnapshotProcesses()
    {
        var map = new Dictionary<int, ProcInfo>();
        IntPtr snap = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snap, ref entry)) return map;
            do
            {
                string key = Path.GetFileNameWithoutExtension(entry.szExeFile).ToLowerInvariant();
                map[(int)entry.th32ProcessID] = new ProcInfo((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, key);
            } while (Process32NextW(snap, ref entry));
        }
        finally
        {
            CloseHandle(snap);
        }
        return map;
    }

    public static string? GetExePath(int pid)
    {
        IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    #endregion
}

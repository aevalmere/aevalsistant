using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using static Aevalsistant.Native;

namespace Aevalsistant
{
    // Finding, checking, and focusing the window that hosts a Claude Code session, whether
    // that is a terminal, an IDE (VS Code, Android Studio), or the Claude desktop app.
    static class Terminals
    {
        // The long-lived process of each agent, used to notice when a session's agent has exited.
        // IDE agents (Cursor, Windsurf) run inside the editor's helper processes, which can be
        // short-lived, so they get none and fall back to the inactivity timeout.
        static readonly Dictionary<string, string[]> AgentProcesses = new Dictionary<string, string[]>
        {
            ["claude"] = new[] { "claude.exe", "node.exe", "bun.exe" },
            ["codex"] = new[] { "codex.exe" },
            ["gemini"] = new[] { "gemini.exe", "node.exe" },
            ["copilot"] = new[] { "copilot.exe", "node.exe" },
            ["cursor"] = new[] { "cursor-agent.exe" },
            ["windsurf"] = new string[0],
            ["opencode"] = new[] { "opencode.exe" },
        };
        static readonly HashSet<string> StopAt =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "explorer.exe", "services.exe", "svchost.exe", "wininit.exe", "winlogon.exe", "userinit.exe" };

        static readonly Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Code.exe"] = "VS Code", ["Code - Insiders.exe"] = "VS Code Insiders", ["Cursor.exe"] = "Cursor",
            ["Windsurf.exe"] = "Windsurf", ["Zed.exe"] = "Zed",
            ["studio64.exe"] = "Android Studio", ["studio.exe"] = "Android Studio",
            ["idea64.exe"] = "IntelliJ IDEA", ["pycharm64.exe"] = "PyCharm", ["webstorm64.exe"] = "WebStorm",
            ["rider64.exe"] = "Rider", ["clion64.exe"] = "CLion", ["goland64.exe"] = "GoLand", ["phpstorm64.exe"] = "PhpStorm",
            ["claude.exe"] = "Claude", ["ChatGPT.exe"] = "ChatGPT", ["Kiro.exe"] = "Kiro", ["Trae.exe"] = "Trae", ["WindowsTerminal.exe"] = "Terminal", ["conhost.exe"] = "Console",
            ["OpenConsole.exe"] = "Terminal", ["wezterm-gui.exe"] = "WezTerm", ["alacritty.exe"] = "Alacritty",
            ["Hyper.exe"] = "Hyper", ["Tabby.exe"] = "Tabby", ["warp.exe"] = "Warp", ["mintty.exe"] = "Git Bash",
        };

        public struct Found { public IntPtr Window; public int AgentPid; public long AgentStart; public int OuterPid; public long OuterStart; public string Host; }

        static readonly HashSet<string> AnyAgent = new HashSet<string>(AgentProcesses.Values.SelectMany(n => n), StringComparer.OrdinalIgnoreCase);

        public struct Proc { public int Pid; public string Name; }

        // Runs inside the short-lived --hook process, whose ancestors are the hook shell,
        // Claude Code, and above that whatever app started Claude Code.
        public static Found FromHookProcess(string project, string agent)
        {
            var found = new Found { Host = "" };
            var chain = Ancestors(Process.GetCurrentProcess().Id);

            // The nearest agent process is this session's own. One further up means another
            // agent started it (`claude -p` from Claude Code's shell); the tray app nests the
            // session under that one's if it knows it.
            var names = AgentProcesses.TryGetValue(agent, out var n) ? n : new string[0];
            int own = chain.FindIndex(p => names.Contains(p.Name, StringComparer.OrdinalIgnoreCase));
            if (own >= 0)
            {
                found.AgentPid = chain[own].Pid;
                found.AgentStart = StartTime(found.AgentPid);
                var outer = chain.Skip(own + 1).FirstOrDefault(p => AnyAgent.Contains(p.Name));
                if (outer.Pid != 0) { found.OuterPid = outer.Pid; found.OuterStart = StartTime(outer.Pid); }
            }

            // Classic console window, or a Windows Terminal window via its pseudo-console owner.
            found.Window = ConsoleHostWindow(out bool classic);
            if (found.Window != IntPtr.Zero)
            {
                found.Host = classic ? "Console" : LabelFor(OwnerPid(found.Window));
                return found;
            }

            // Otherwise the nearest ancestor that owns windows: Code.exe, studio64.exe,
            // Claude.exe and so on. With several windows (two VS Code workspaces), prefer the
            // one whose title names the project; window order is most recently used first.
            var windows = VisibleWindowsByPid();
            foreach (var p in chain)
            {
                if (StopAt.Contains(p.Name)) break;
                if (!windows.TryGetValue(p.Pid, out var list)) continue;
                found.Window = PickByTitle(list, project);
                found.Host = LabelFor(p.Pid, p.Name);
                break;
            }
            return found;
        }

        static IntPtr PickByTitle(List<IntPtr> list, string needle)
        {
            if (!string.IsNullOrEmpty(needle))
                foreach (var h in list)
                    if (Title(h).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return h;
            return list[0];
        }

        static IntPtr ConsoleHostWindow(out bool classic)
        {
            classic = false;
            if (!AttachConsole(ATTACH_PARENT_PROCESS)) return IntPtr.Zero;
            try
            {
                IntPtr h = GetConsoleWindow();
                if (h == IntPtr.Zero) return IntPtr.Zero;
                if (IsWindowVisible(h)) { classic = true; return h; }
                IntPtr owner = GetAncestor(h, GA_ROOTOWNER);
                return owner != IntPtr.Zero && owner != h && IsWindowVisible(owner) ? owner : IntPtr.Zero;
            }
            finally { FreeConsole(); }
        }

        static int OwnerPid(IntPtr h)
        {
            GetWindowThreadProcessId(h, out uint pid);
            return (int)pid;
        }

        public static string LabelFor(int pid, string exeName = null)
        {
            string path = ImagePath(pid);
            string name = exeName ?? (path.Length > 0 ? Path.GetFileName(path) : "");
            if (name.Length > 0 && Names.TryGetValue(name, out var known)) return known;
            if (path.Length > 0)
            {
                try
                {
                    string desc = FileVersionInfo.GetVersionInfo(path).FileDescription;
                    if (!string.IsNullOrWhiteSpace(desc) && desc.Length <= 32) return desc.Trim();
                }
                catch (FileNotFoundException) { /* process exited */ }
            }
            return name.Length > 0 ? Path.GetFileNameWithoutExtension(name) : "";
        }

        public static string ImagePath(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return "";
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : "";
            }
            finally { CloseHandle(h); }
        }

        public static List<Proc> Ancestors(int pid)
        {
            var parent = new Dictionary<int, int>();
            var name = new Dictionary<int, string>();
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == INVALID_HANDLE_VALUE) return new List<Proc>();
            try
            {
                var pe = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32W)) };
                for (bool ok = Process32FirstW(snap, ref pe); ok; ok = Process32NextW(snap, ref pe))
                {
                    parent[(int)pe.th32ProcessID] = (int)pe.th32ParentProcessID;
                    name[(int)pe.th32ProcessID] = pe.szExeFile;
                }
            }
            finally { CloseHandle(snap); }

            var chain = new List<Proc>();
            var seen = new HashSet<int> { pid };
            int cur = pid;
            long curStart = StartTime(pid);
            while (parent.TryGetValue(cur, out int up) && up != 0 && seen.Add(up) && name.ContainsKey(up) && chain.Count < 40)
            {
                // A "parent" that started after its child is some other program that reused the
                // pid of a parent that has exited. Start time 0 means it could not be read.
                long upStart = StartTime(up);
                if (upStart != 0 && curStart != 0 && upStart > curStart) break;
                chain.Add(new Proc { Pid = up, Name = name[up] });
                cur = up;
                curStart = upStart;
            }
            return chain;
        }

        static string Title(IntPtr h)
        {
            int n = GetWindowTextLength(h);
            if (n <= 0) return "";
            var sb = new StringBuilder(n + 1);
            GetWindowText(h, sb, sb.Capacity);
            return sb.ToString();
        }

        static Dictionary<int, List<IntPtr>> VisibleWindowsByPid()
        {
            var map = new Dictionary<int, List<IntPtr>>();
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero || GetWindowTextLength(h) == 0) return true;
                if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
                if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return true;
                GetWindowThreadProcessId(h, out uint wpid);
                if (!map.TryGetValue((int)wpid, out var list)) map[(int)wpid] = list = new List<IntPtr>();
                list.Add(h);
                return true;
            }, IntPtr.Zero);
            return map;
        }

        public static long StartTime(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return 0;
            try { return GetProcessTimes(h, out long c, out _, out _, out _) ? c : 0; }
            finally { CloseHandle(h); }
        }

        public static bool IsAlive(int pid, long start)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                if (!GetExitCodeProcess(h, out uint code) || code != STILL_ACTIVE) return false;
                // Guards against a recycled pid now belonging to some other program.
                return start == 0 || !GetProcessTimes(h, out long c, out _, out _, out _) || c == start;
            }
            finally { CloseHandle(h); }
        }

        public static bool Focus(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);

            IntPtr fg = GetForegroundWindow();
            uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
            uint me = GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
            try
            {
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached) AttachThreadInput(me, fgThread, false);
            }
            if (GetForegroundWindow() != hwnd) SwitchToThisWindow(hwnd, true);
            return true;
        }
    }
}

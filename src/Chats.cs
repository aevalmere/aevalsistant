using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using static Aevalsistant.Native;

namespace Aevalsistant
{
    // Turns "is this chat window generating?" samples into session events. No Windows code, so
    // the rules are testable anywhere.
    sealed class ChatTracker
    {
        sealed class Watch { public int Misses; }
        readonly Dictionary<long, Watch> watching = new Dictionary<long, Watch>();

        public bool IsWatching(long hwnd) => watching.ContainsKey(hwnd);
        public IEnumerable<long> Watched => watching.Keys.ToList();

        public static string SessionId(long hwnd) => "chat:" + hwnd;

        // generating: true/false from the Stop button, null when the window could not be read.
        public HookEvent Observe(long hwnd, int pid, string app, bool exists, bool? generating, bool foreground)
        {
            HookEvent Ev(string name) => new HookEvent
            {
                Name = name, SessionId = SessionId(hwnd), Agent = "chat", Label = "Chat", Host = app,
                Hwnd = hwnd, Pid = pid,
            };

            if (!exists)
            {
                if (!watching.Remove(hwnd)) return null;
                return Ev("SessionEnd");
            }
            if (generating == null) return null;
            if (generating.Value)
            {
                if (watching.TryGetValue(hwnd, out var w)) { w.Misses = 0; return null; }
                watching[hwnd] = new Watch();
                return Ev("UserPromptSubmit");
            }
            if (!watching.TryGetValue(hwnd, out var seen)) return null;
            // Two misses in a row, so a re-render that briefly drops the button does not count.
            if (++seen.Misses < 2) return null;
            watching.Remove(hwnd);
            // Finished while you were looking at it: no notification needed.
            return Ev(foreground ? "Interrupt" : "Stop");
        }
    }

    // Claude and ChatGPT desktop chats have no hooks. While a reply is being written they show
    // a Stop button, which Windows accessibility (UI Automation) can see. The watcher looks only
    // when you switch away from one of these apps, and then every few seconds until the reply is
    // done, so nothing is read while you are not waiting on a reply.
    sealed class ChatWatcher : IDisposable
    {
        static readonly Dictionary<string, string> Apps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude.exe"] = "Claude",
            ["ChatGPT.exe"] = "ChatGPT",
        };
        static readonly string[] StopNames = { "Stop response", "Stop streaming", "Stop generating", "Stop" };

        const uint EVENT_SYSTEM_FOREGROUND = 3, WINEVENT_OUTOFCONTEXT = 0;
        delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);

        public event Action<HookEvent> Changed;   // raised on the UI thread
        public event Action Failed;

        readonly SynchronizationContext ui = SynchronizationContext.Current;
        readonly ChatTracker tracker = new ChatTracker();
        readonly Func<long, bool> trackedByHooks;
        readonly WinEventProc proc;
        readonly object gate = new object();
        readonly Queue<IntPtr> toCheck = new Queue<IntPtr>();
        readonly Thread worker;
        IntPtr hook, lastForeground;
        volatile bool stopped;

        // trackedByHooks: a window that already has a busy hook session (Claude Code in the
        // desktop app's Code tab) is left alone so it does not show up twice.
        public ChatWatcher(Func<long, bool> trackedByHooks)
        {
            this.trackedByHooks = trackedByHooks;
            proc = OnForeground;
            hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, proc, 0, 0, WINEVENT_OUTOFCONTEXT);
            lastForeground = GetForegroundWindow();
            worker = new Thread(Loop) { IsBackground = true, Name = "chat-watch" };
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();
        }

        void OnForeground(IntPtr h, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            IntPtr left = lastForeground;
            lastForeground = hwnd;
            if (left == IntPtr.Zero || AppOf(left) == null) return;
            lock (gate) { toCheck.Enqueue(left); Monitor.Pulse(gate); }
        }

        void Loop()
        {
            while (!stopped)
            {
                IntPtr next = IntPtr.Zero;
                lock (gate)
                {
                    if (toCheck.Count == 0) Monitor.Wait(gate, 2500);
                    if (toCheck.Count > 0) next = toCheck.Dequeue();
                }
                if (stopped) return;

                try
                {
                    if (next != IntPtr.Zero)
                    {
                        // Give the app a moment; Chromium builds its accessibility tree on first request.
                        Thread.Sleep(500);
                        Sample(next, retryIfEmpty: true);
                    }
                    foreach (long h in tracker.Watched) Sample(new IntPtr(h), retryIfEmpty: false);
                }
                catch (Exception e) when (e is System.IO.FileNotFoundException || e is TypeLoadException)
                {
                    // UI Automation is part of every Windows .NET install; if it is missing anyway,
                    // chat watching switches itself off instead of taking the app down.
                    stopped = true;
                    ui.Post(_ => Failed?.Invoke(), null);
                    return;
                }
            }
        }

        void Sample(IntPtr hwnd, bool retryIfEmpty)
        {
            long id = hwnd.ToInt64();
            bool exists = IsWindow(hwnd);
            string app = exists ? AppOf(hwnd) : null;
            if (exists && app == null) return;
            if (exists && !tracker.IsWatching(id) && trackedByHooks(id)) return;

            bool? generating = exists ? Generating(hwnd) : null;
            if (generating == false && retryIfEmpty && !tracker.IsWatching(id))
            {
                Thread.Sleep(700);
                generating = Generating(hwnd);
            }
            GetWindowThreadProcessId(hwnd, out uint owner);
            var e = tracker.Observe(id, (int)owner, app ?? "", exists, generating, GetForegroundWindow() == hwnd);
            if (e != null) ui.Post(_ => Changed?.Invoke(e), null);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static bool? Generating(IntPtr hwnd)
        {
            try
            {
                var root = AutomationElement.FromHandle(hwnd);
                var names = new OrCondition(StopNames.Select(n => (Condition)new PropertyCondition(AutomationElement.NameProperty, n)).ToArray());
                var button = new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button), names);
                var found = root.FindFirst(TreeScope.Descendants, button);
                return found != null && !found.Current.IsOffscreen;
            }
            catch (ElementNotAvailableException) { return null; }   // window closed mid-read
            catch (COMException) { return null; }                   // app not answering accessibility calls
            catch (InvalidOperationException) { return null; }
        }

        static string AppOf(IntPtr hwnd) => AppOf(hwnd, out _);

        static string AppOf(IntPtr hwnd, out int pid)
        {
            GetWindowThreadProcessId(hwnd, out uint p);
            pid = (int)p;
            try
            {
                using (var proc = System.Diagnostics.Process.GetProcessById(pid))
                    return Apps.TryGetValue(proc.ProcessName + ".exe", out var name) ? name : null;
            }
            catch (ArgumentException) { return null; }   // already exited
        }

        public void Dispose()
        {
            stopped = true;
            lock (gate) Monitor.Pulse(gate);
            if (hook != IntPtr.Zero) UnhookWinEvent(hook);
            hook = IntPtr.Zero;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Aevalsistant
{
    sealed class TrayApp : ApplicationContext
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        readonly SynchronizationContext ui;
        readonly Settings settings = Settings.Load(App.SettingsPath);
        readonly SessionBook book = new SessionBook();
        readonly List<ToastRequest> pending = new List<ToastRequest>();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly ToastWindow toast;
        readonly AwakeRequest awake = new AwakeRequest();
        readonly System.Windows.Forms.Timer sweep = new System.Windows.Forms.Timer { Interval = 5000 };
        readonly string hookCommand = HookCommand();

        string lidProblem, chatProblem;
        bool quitting, labelClicked;
        readonly Dictionary<string, string> agentProblems = new Dictionary<string, string>();
        readonly HashSet<string> justConnected = new HashSet<string>();
        int sweeps;
        ChatWatcher chats;
        readonly System.Windows.Forms.Timer updateTimer = new System.Windows.Forms.Timer { Interval = 2 * 60 * 1000 };
        string updateStatus, pendingUpdate;
        bool checkingUpdate;
        volatile HashSet<long> busyWindows = new HashSet<long>();   // read by the chat watcher thread
        Icon trayIcon;
        bool? trayAwake;
        readonly bool hasLid = LidAction.HasLid();
        readonly LidWatcher lid;
        // With the lid shut when the agents finish, the plan's own lid action (sleep) only runs
        // once the lid moves again, so the app runs it a minute later if nothing restarted.
        readonly System.Windows.Forms.Timer lidSleep = new System.Windows.Forms.Timer { Interval = 60 * 1000 };
        uint? lidActionDue;
        SettingsWindow settingsWindow;
        Note updateNote, lidNote, chatNote;
        readonly Dictionary<string, Toggle> agentToggles = new Dictionary<string, Toggle>();
        readonly TimeLog timeLog = TimeLog.Load(App.TimePath);
        // Watched apps open right now, by name, with when each was first seen open.
        readonly Dictionary<string, DateTime> appsOpen = new Dictionary<string, DateTime>();
        readonly Dictionary<string, Toggle> appToggles = new Dictionary<string, Toggle>();
        Toggle watchToggle, doneToggle, asksToggle;
        Choice notifyChoice;
        Note timeNote;
        AppPicker picker;
        // Agent commands: holds on keep-awake, and a sleep, restart, or shutdown counting down.
        readonly AwakeHolds holds = new AwakeHolds();
        readonly System.Windows.Forms.Timer powerTimer = new System.Windows.Forms.Timer();
        string powerKind, powerBy;
        ToastRequest powerCard;
        DateTime powerDue;

        public TrayApp()
        {
            if (!(SynchronizationContext.Current is WindowsFormsSynchronizationContext))
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            ui = SynchronizationContext.Current;

            toast = new ToastWindow { ExpandOnHover = settings.ExpandOnHover };
            toast.Clicked += Activate;
            toast.AltTabbed += () => Activate(ToastArt.HitHead);
            toast.Expired += () => { pending.Clear(); toast.Hide(); };
            toast.Dismissed += () => { pending.Clear(); toast.Hide(); };

            // Repair a lid override left behind by a crash or power loss.
            if (settings.LidSaved.Length > 0 && LidAction.Restore(settings.LidSaved)) { settings.LidSaved = ""; Save(); }
            if (hasLid)
            {
                lid = new LidWatcher();
                lid.Changed += closed => { if (!closed) { lidSleep.Stop(); lidActionDue = null; } };
            }
            lidSleep.Tick += (s, e) => SleepIfLidStillShut();

            if (settings.Hooks) SyncAgents(true);
            SyncGuide();
            powerTimer.Tick += (s, e) => FirePower();
            if (settings.Chats) StartChats();
            ScanApps();
            SetStartup(settings.Startup);

            menu.Renderer = new PaperMenuRenderer();
            menu.ShowCheckMargin = false;
            menu.ShowImageMargin = true;
            menu.Font = new Font(Theme.Family, 9.75f);
            menu.Padding = new Padding(0, 6, 0, 6);
            menu.Opening += (s, e) => { BuildMenu(); e.Cancel = false; };
            menu.ItemClicked += (s, e) => labelClicked = e.ClickedItem.Tag is string;
            menu.Closing += (s, e) =>
            {
                if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked && labelClicked) e.Cancel = true;
                labelClicked = false;
            };
            int corner = Native.DWMWCP_ROUNDSMALL;
            Native.DwmSetWindowAttribute(menu.Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, 4);

            tray.ContextMenuStrip = menu;
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowMenuAtCursor(); };
            RefreshState();
            tray.Visible = true;

            sweep.Tick += (s, e) => Sweep();
            sweep.Start();
            new Thread(Listen) { IsBackground = true, Name = "hook-pipe" }.Start();
            new Thread(ListenCommands) { IsBackground = true, Name = "command-pipe" }.Start();

            Updater.CleanUp();
            updateTimer.Tick += (s, e) =>
            {
                updateTimer.Interval = 6 * 60 * 60 * 1000;   // first check two minutes after start, then every six hours
                if (pendingUpdate == null) Updater.CleanUp();   // again, in case the copy that handed off was still exiting at start
                if (settings.AutoUpdate) CheckForUpdates(manual: false);
            };
            updateTimer.Start();

            string version = Updater.Current.ToString(3);
            if (settings.LastVersion.Length > 0 && settings.LastVersion != version)
                Enqueue(new ToastRequest
                {
                    Kind = ToastKind.Info, Title = "Aevalsistant updated to " + version,
                    Detail = justConnected.Count > 0 ? "Restart open agent sessions once to pick up the new hooks." : "Your settings and hooks carried over.",
                });
            if (settings.LastVersion != version) { settings.LastVersion = version; Save(); }

            if (!settings.Welcomed)
            {
                settings.Welcomed = true;
                Save();
                Enqueue(new ToastRequest
                {
                    Kind = ToastKind.Info, Title = "Aevalsistant is running",
                    Detail = "Restart open Claude Code sessions once so they report here. Settings are in the tray menu, next to the clock.",
                });
            }
        }

        // ---- hook traffic -------------------------------------------------------------

        void Listen()
        {
            while (!quitting)
            {
                try
                {
                    using (var server = new NamedPipeServerStream(App.PipeName, PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.None))
                    {
                        server.WaitForConnection();
                        var ms = new MemoryStream();
                        var buf = new byte[1 << 16];
                        int n;
                        while ((n = server.Read(buf, 0, buf.Length)) > 0 && ms.Length < (8 << 20)) ms.Write(buf, 0, n);
                        string text = Encoding.UTF8.GetString(ms.ToArray());
                        ui.Post(_ => OnMessage(text), null);
                    }
                }
                catch (IOException) { Thread.Sleep(200); }   // a client vanished mid-write
            }
        }

        // Agent commands come in on their own pipe, one message each, and get one message back.
        void ListenCommands()
        {
            while (!quitting)
            {
                try
                {
                    CommandPipe.ServeOne(App.CommandPipeName, text =>
                    {
                        string reply = "Refused: not an agent command.";
                        var req = AgentRequest.From(text);
                        if (req != null) ui.Send(_ => reply = HandleAgent(req), null);
                        return reply;
                    });
                }
                catch (IOException) { Thread.Sleep(200); }   // the client left before the answer
            }
        }

        void OnMessage(string text)
        {
            if (text.StartsWith("{\"cmd\":\"hello\"", StringComparison.Ordinal))
            {
                Enqueue(new ToastRequest { Kind = ToastKind.Info, Title = "Aevalsistant is already running", Detail = "It lives in the tray, next to the clock." });
                return;
            }
            if (text.StartsWith("{\"cmd\":\"quit\"", StringComparison.Ordinal)) { Quit(); return; }

            var e = HookEvent.FromEnvelope(text);
            if (e != null) Ingest(e);
        }

        void Ingest(HookEvent e)
        {
            var req = book.Apply(e, DateTime.UtcNow);
            if (e.Name == "UserPromptSubmit" || e.Name == "SessionEnd") Withdraw(e.SessionId);
            if (req != null) Enqueue(req);
            else if (toast.Showing) ShowTop(false);   // keep the list under the main row current
            RefreshState();
        }

        bool CanFocus(string sessionId, long hwnd)
        {
            long h = book.Get(sessionId)?.Hwnd ?? hwnd;
            return h != 0 && Native.IsWindow(new IntPtr(h));
        }

        // Alt+Tab is taken over only when it would go somewhere new. With the session's window
        // already in front, it stays the normal window switcher.
        bool CanJump(string sessionId, long hwnd)
        {
            if (!CanFocus(sessionId, hwnd)) return false;
            long h = book.Get(sessionId)?.Hwnd ?? hwnd;
            IntPtr fg = Native.GetForegroundWindow();
            return fg == IntPtr.Zero || Native.GetAncestor(fg, Native.GA_ROOT) != new IntPtr(h);
        }

        // The agent's own window is the one in front, so you are already looking at it.
        bool InFront(ToastRequest req)
        {
            long h = book.Get(req.SessionId)?.Hwnd ?? req.Hwnd;
            if (h == 0) return false;
            IntPtr fg = Native.GetForegroundWindow();
            return fg != IntPtr.Zero && Native.GetAncestor(fg, Native.GA_ROOT) == new IntPtr(h);
        }

        void FocusTarget(string sessionId, long hwnd)
        {
            long h = book.Get(sessionId)?.Hwnd ?? hwnd;
            if (h != 0) Terminals.Focus(new IntPtr(h));
        }

        // ---- toasts -------------------------------------------------------------------

        // Why a card would not show, or null when it would.
        string Unwanted(ToastRequest req) =>
            !settings.Wants(req) ? "notifications are off (Aevalsistant settings)"
            : settings.NotifyAway && req.Kind != ToastKind.Info && InFront(req) ? "the user is already looking at it"
            : null;

        void Enqueue(ToastRequest req)
        {
            if (Unwanted(req) != null)
            {
                // No card for this, but the list under a card already showing stays current.
                if (toast.Showing) ShowTop(false);
                return;
            }
            if (req.SessionId != null) pending.RemoveAll(p => p.SessionId == req.SessionId);
            pending.Add(req);
            if (req.WantsSnippet)
            {
                // The Stop hook can fire a moment before the final message is flushed to disk.
                var t = new System.Windows.Forms.Timer { Interval = 300 };
                t.Tick += (s, a) =>
                {
                    t.Dispose();
                    req.Detail = Snippet.Clean(SafeLastText(book.Get(req.SessionId)?.Transcript));
                    if (req.Detail.Length == 0) req.Detail = "Ready for your next prompt.";
                    req.WantsSnippet = false;
                    if (!pending.Contains(req)) return;
                    if (settings.Sound) Sound.Play(req.Kind);
                    ShowTop();
                };
                t.Start();
                return;
            }
            if (settings.Sound) Sound.Play(req.Kind);
            ShowTop();
        }

        ToastContent shown;

        // The main row is the newest status change; under it, every other session this app
        // knows about, so one glance covers all of them.
        void ShowTop(bool restartCountdown = true)
        {
            var top = pending.LastOrDefault(p => !p.WantsSnippet);
            if (top == null) { if (pending.Count == 0) toast.Hide(); return; }

            var c = new ToastContent
            {
                Title = top.Title, Detail = top.Detail, Kind = top.Kind, Host = top.Host ?? "",
                Keycap = settings.AltTab && CanJump(top.SessionId, top.Hwnd),
            };
            var tree = book.Tree(top.SessionId, settings.ShowChildren);
            var now = DateTime.UtcNow;
            foreach (var r in tree.Take(ToastArt.MaxRows)) c.Rows.Add(RowFor(r, now));
            if (tree.Count > ToastArt.MaxRows)
                c.Overflow = (tree.Count - ToastArt.MaxRows) + " more in the tray menu";

            shown = c;
            double seconds = top == powerCard ? Math.Max(1, (powerDue - DateTime.UtcNow).TotalSeconds) : settings.Seconds(top.Kind);
            toast.Show(c, restartCountdown ? seconds : -1);
        }

        // A subagent row focuses its session's window, since that is where it runs.
        static ToastRow RowFor(TreeRow r, DateTime now) => r.Subagent != null
            ? new ToastRow
            {
                SessionId = r.Session.Id, Hwnd = r.Session.Hwnd, Depth = r.Depth, State = RowState.Working,
                Name = r.Subagent.Type.Length > 0 ? r.Subagent.Type : "subagent", Status = Status.Text(r.Subagent, now),
            }
            : new ToastRow
            {
                SessionId = r.Session.Id, Hwnd = r.Session.Hwnd, Depth = r.Depth, State = Status.State(r.Session),
                Name = r.Session.Project, Host = r.Session.Where, Status = Status.Text(r.Session, now),
            };

        void Withdraw(string sessionId)
        {
            if (pending.RemoveAll(p => p.SessionId == sessionId) == 0) return;
            if (pending.Count == 0) toast.Hide();
            else if (toast.Showing) ShowTop(false);
        }

        // Click on the main row or Alt+Tab: that session. Click on a listed row: that one.
        void Activate(int target)
        {
            // The countdown card for a sleep or shutdown cancels it.
            if (powerCard != null && pending.LastOrDefault(p => !p.WantsSnippet) == powerCard)
            {
                CancelPower();
                return;
            }
            string sid = null; long hwnd = 0;
            if (target >= 0 && shown != null && target < shown.Rows.Count)
            {
                sid = shown.Rows[target].SessionId;
                hwnd = shown.Rows[target].Hwnd;
            }
            else
            {
                var top = pending.LastOrDefault(p => !p.WantsSnippet);
                sid = top?.SessionId;
                hwnd = top?.Hwnd ?? 0;
            }
            pending.Clear();
            toast.Hide();
            if (sid != null || hwnd != 0) FocusTarget(sid, hwnd);
        }

        static string SafeLastText(string transcript)
        {
            try { return Transcript.LastAssistantText(Transcript.TailLines(transcript)); }
            catch (IOException) { return ""; }
            catch (UnauthorizedAccessException) { return ""; }
        }

        // ---- sessions and power ---------------------------------------------------------

        void Sweep()
        {
            // Every 10 minutes, connect agents installed since the app started.
            if (++sweeps % 120 == 0 && settings.Hooks) SyncAgents(true);
            TryApplyUpdate();
            bool changed = book.Sweep(DateTime.UtcNow,
                s => Terminals.IsAlive(s.Pid, s.PidStart),
                s =>
                {
                    try
                    {
                        if (string.IsNullOrEmpty(s.Transcript) || !File.Exists(s.Transcript)) return null;
                        var mtime = File.GetLastWriteTimeUtc(s.Transcript);
                        var probe = new TranscriptProbe { Modified = mtime };
                        if (mtime > s.TranscriptSeen) probe.EndsInInterrupt = Transcript.EndsInInterrupt(Transcript.TailLines(s.Transcript));
                        return probe;
                    }
                    catch (IOException) { return null; }
                    catch (UnauthorizedAccessException) { return null; }
                },
                (s, agentId) =>
                {
                    string path = s.SubagentTranscript(agentId);
                    try { return path.Length > 0 && File.Exists(path) ? File.GetLastWriteTimeUtc(path) : (DateTime?)null; }
                    catch (IOException) { return null; }
                    catch (UnauthorizedAccessException) { return null; }
                });
            bool appsChanged = ScanApps() | holds.Sweep(DateTime.UtcNow, h => Terminals.IsAlive(h.Pid, h.PidStart));
            var now = DateTime.UtcNow;
            var active = book.All.Where(x => x.Busy).Select(x => TimeKeys.Agent(x.Agent)).Concat(appsOpen.Keys.Select(TimeKeys.App)).ToList();
            book.Credit(timeLog.Tick(now, TimeLog.DayString(DateTime.Now), active));
            if (sweeps % 12 == 0) SaveTime();
            if (settings.Remind)
                foreach (var r in book.Reminders(now, TimeSpan.FromMinutes(Settings.RemindMinutes[settings.RemindAfter]))) Enqueue(r);
            if (appsChanged) RefreshState();
            RefreshSettingsNotes();

            int before = pending.Count;
            pending.RemoveAll(p => p.SessionId != null && book.Get(p.SessionId) == null);
            if (pending.Count != before) { if (pending.Count == 0) toast.Hide(); else if (toast.Showing) ShowTop(); }
            if (changed) { RefreshState(); if (toast.Showing) ShowTop(false); }
        }

        void StartChats()
        {
            if (chats != null) return;
            chats = new ChatWatcher(hwnd => busyWindows.Contains(hwnd));
            chats.Changed += Ingest;
            chats.Failed += reason =>
            {
                chats?.Dispose();   // its foreground hook would otherwise call into a collected delegate
                chats = null;
                chatProblem = "Chat watching stopped: " + reason;
                RefreshSettingsNotes();
            };
        }

        void StopChats()
        {
            chats?.Dispose();
            chats = null;
            foreach (var s in book.All.Where(x => x.Agent == "chat").ToList())
                Ingest(new HookEvent { Name = "SessionEnd", SessionId = s.Id });
        }

        // ---- agent commands ---------------------------------------------------------------

        static readonly Dictionary<string, string> PowerVerb = new Dictionary<string, string>
        {
            ["sleep"] = "put to sleep", ["restart"] = "restart", ["shutdown"] = "shut down",
        };

        string HandleAgent(AgentRequest req)
        {
            var session = book.Find(req.Pid, req.PidStart, req.Pwd);
            string key = req.Pid != 0 ? "pid:" + req.Pid + ":" + req.PidStart : "cwd:" + req.Pwd.ToLowerInvariant();
            string label = session?.Project ?? (req.Pwd.Length > 0 ? new Session { Cwd = req.Pwd }.Project : "An agent");
            switch (req.Action)
            {
                case "awake":
                    if (!settings.AgentAwake) return "Refused: agents may not keep this computer awake (Aevalsistant settings).";
                    var now = DateTime.UtcNow;
                    holds.Add(new Hold
                    {
                        Key = key, Label = label, Reason = req.Reason, Since = now, Pid = req.Pid, PidStart = req.PidStart,
                        Until = req.Minutes > 0 ? now.AddMinutes(req.Minutes) : (DateTime?)null,
                    });
                    RefreshState();
                    return req.Minutes > 0
                        ? "Keeping the computer awake for " + Status.Duration(req.Minutes * 60) + (req.Reason.Length > 0 ? " (" + req.Reason + ")" : "") + "."
                        : "Keeping the computer awake until you run --awake off.";

                case "release":
                    if (!holds.Release(key)) return "Nothing to release.";
                    RefreshState();
                    return "Released.";

                case "sleep":
                case "restart":
                case "shutdown":
                    string verb = PowerVerb[req.Action];
                    if (!(req.Action == "sleep" ? settings.AgentSleep : settings.AgentPower))
                        return "Refused: agents may not " + verb + " this computer (Aevalsistant settings).";
                    if (powerKind != null) return "Refused: a " + powerKind + " is already pending.";
                    if (holds.Release(key)) RefreshState();
                    var busy = PowerBlockers(req, session);
                    if (busy.Count > 0) return "Refused: still busy: " + string.Join(", ", busy) + ".";
                    StartPower(req, session, label);
                    return PowerTitle(req.Action) + " unless the user cancels.";

                case "notify":
                    var card = new ToastRequest
                    {
                        Kind = req.NeedsYou ? ToastKind.NeedsYou : ToastKind.Done, Title = req.Title, Detail = req.Detail, FromAgent = true,
                        SessionId = session?.Id, Hwnd = session?.Hwnd ?? req.Hwnd, Host = session?.Where ?? req.Host,
                    };
                    string why = Unwanted(card);
                    if (why != null) return "Not shown: " + why + ".";
                    Enqueue(card);
                    return "Shown.";

                case "status":
                    string reason = AwakeReason(book.BusyCount);
                    return "Aevalsistant " + Updater.Current.ToString(3) + ": " + (reason.Length > 0 ? reason : "idle")
                        + (settings.KeepAwake ? "" : ", keep-awake off") + ". Notify: "
                        + new[] { "always", "if needed", "agent decides", "never" }[settings.NotifyMode]
                        + (powerKind != null ? ". A " + powerKind + " is pending" : "") + ".";
            }
            return "Refused: unknown command.";
        }

        // What would be cut off: other busy sessions, open watched apps, other agents' holds.
        List<string> PowerBlockers(AgentRequest req, Session asking)
        {
            var list = book.BusyBlockers(req.Pid, req.PidStart, asking?.Id).Select(x => x.Project + (x.Agent != "claude" ? " (" + Agents.Get(x.Agent).Name + ")" : "")).ToList();
            list.AddRange(appsOpen.Keys.Select(n => AppLabel(n) + " open"));
            list.AddRange(holds.All.Select(h => h.Label + " keeping awake"));
            return list;
        }

        static string PowerTitle(string kind) =>
            kind == "sleep" ? "Going to sleep in 15 seconds" : kind == "restart" ? "Restarting in 60 seconds" : "Shutting down in 60 seconds";

        AgentRequest powerReq;

        void StartPower(AgentRequest req, Session asking, string label)
        {
            powerKind = req.Action;
            powerReq = req;
            powerBy = asking?.Id;
            int seconds = req.Action == "sleep" ? 15 : 60;
            powerDue = DateTime.UtcNow.AddSeconds(seconds);
            powerTimer.Interval = seconds * 1000;
            powerTimer.Start();
            powerCard = new ToastRequest
            {
                Kind = ToastKind.Info, Title = PowerTitle(req.Action), Detail = label + " asked for it. Click this card to cancel.",
            };
            pending.Add(powerCard);
            if (settings.Sound) Sound.Play(ToastKind.NeedsYou);
            ShowTop();
        }

        void ClearPower()
        {
            powerTimer.Stop();
            if (powerCard != null) pending.Remove(powerCard);
            powerKind = null;
            powerCard = null;
            powerReq = null;
            powerBy = null;
        }

        void CancelPower()
        {
            if (powerKind == null) return;
            string kind = powerKind == "shutdown" ? "shutdown" : powerKind;
            ClearPower();
            pending.Clear();
            toast.Hide();
            Enqueue(new ToastRequest { Kind = ToastKind.Info, Title = "Cancelled", Detail = "The " + kind + " was cancelled." });
        }

        // Checks once more before acting: work that started during the countdown wins.
        void FirePower()
        {
            string kind = powerKind;
            var req = powerReq;
            string by = powerBy;
            ClearPower();
            if (kind == null || req == null) return;
            if (pending.Count == 0) toast.Hide(); else ShowTop(false);
            var busy = PowerBlockers(req, book.Get(by));
            if (busy.Count > 0)
            {
                Enqueue(new ToastRequest { Kind = ToastKind.Info, Title = "Did not " + PowerVerb[kind], Detail = string.Join(", ", busy) + " became busy." });
                return;
            }
            SaveTime();
            Save();
            if (kind == "sleep") { Battery.Suspend(hibernate: false); return; }
            try
            {
                Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), kind == "restart" ? "/r /t 0" : "/s /t 0")
                {
                    CreateNoWindow = true, UseShellExecute = false,
                });
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                Enqueue(new ToastRequest { Kind = ToastKind.Info, Title = "Could not " + PowerVerb[kind], Detail = e.Message });
            }
        }

        string GuidePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "CLAUDE.md");

        // Keeps the Aevalsistant section of ~/.claude/CLAUDE.md in step with Settings. The file is
        // backed up once before the first change, like the agents' hook files.
        void SyncGuide(bool remove = false)
        {
            string path = GuidePath;
            try
            {
                string text = File.Exists(path) ? File.ReadAllText(path) : null;
                string exe = hookCommand.EndsWith(" --hook", StringComparison.Ordinal) ? hookCommand.Substring(0, hookCommand.Length - 7) : hookCommand;
                string next = AgentGuide.Apply(text, remove ? "" : AgentGuide.Block(exe, settings));
                if (next == null || next == text) return;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string backup = path + ".aevalsistant.bak";
                if (text != null && !File.Exists(backup)) File.WriteAllText(backup, text);
                WriteConfig(path, next);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // ---- watched apps -----------------------------------------------------------------

        // Which watched apps are open. True when that changed.
        bool ScanApps()
        {
            var names = settings.WatchAppsOn && settings.WatchApps.Any(a => a.On) ? RunningApp.Names() : new HashSet<string>();
            var open = settings.WatchAppsOn
                ? settings.WatchApps.Where(a => a.On && names.Any(n => WatchMatch.Matches(a.Name, n))).Select(a => a.Name).ToList()
                : new List<string>();
            bool changed = open.Count != appsOpen.Count || open.Any(n => !appsOpen.ContainsKey(n));
            foreach (var gone in appsOpen.Keys.Where(k => !open.Contains(k)).ToList()) appsOpen.Remove(gone);
            foreach (var n in open) if (!appsOpen.ContainsKey(n)) appsOpen[n] = DateTime.UtcNow;
            return changed;
        }

        string AppLabel(string name) => settings.WatchApps.FirstOrDefault(a => a.Name == name)?.Label ?? name;

        // "2 agents working, Blender open"
        string AwakeReason(int working)
        {
            var parts = new List<string>();
            if (working > 0) parts.Add(Plural.Agents(working) + " working");
            if (appsOpen.Count > 0) parts.Add(string.Join(" and ", appsOpen.Keys.Select(AppLabel)) + " open");
            foreach (var h in holds.All) parts.Add(h.Label + " holding" + (h.Reason.Length > 0 ? " (" + h.Reason + ")" : ""));
            return string.Join(", ", parts);
        }

        string AppNote(WatchedApp a)
        {
            if (!appsOpen.TryGetValue(a.Name, out var since)) return a.On ? "Not open" : "Off";
            double secs = (DateTime.UtcNow - since).TotalSeconds;
            return secs < 60 ? "Open" : "Open " + Status.Duration(secs);
        }

        void OpenPicker()
        {
            if (picker != null && !picker.IsDisposed) { picker.Activate(); return; }
            picker = new AppPicker(name => settings.WatchApps.Any(a => a.Name == name && a.On), AddApp, RemoveApp);
            picker.FormClosed += (s, e) => picker = null;
            picker.Show();
        }

        void AddApp(RunningApp r)
        {
            var known = settings.WatchApps.FirstOrDefault(a => a.Name == r.Name);
            if (known != null)
            {
                known.On = true;
                if (appToggles.TryGetValue(known.Name, out var t)) t.On = true;
            }
            else
            {
                var app = new WatchedApp { Name = r.Name, Label = r.Label, On = true };
                var lastApp = settings.WatchApps.LastOrDefault(a => appToggles.ContainsKey(a.Name));
                settings.WatchApps.Add(app);
                if (settingsWindow != null && watchToggle != null)
                {
                    var after = lastApp != null ? appToggles[lastApp.Name] : watchToggle;
                    appToggles[app.Name] = settingsWindow.InsertToggle(after, app.Label, AppNote(app), true, v => SetAppOn(app, v), watchToggle);
                }
            }
            AppsChanged();
        }

        void RemoveApp(string name)
        {
            settings.WatchApps.RemoveAll(a => a.Name == name);
            if (appToggles.TryGetValue(name, out var t)) { settingsWindow?.Remove(t); appToggles.Remove(name); }
            AppsChanged();
        }

        void SetAppOn(WatchedApp app, bool on)
        {
            app.On = on;
            AppsChanged();
        }

        void AppsChanged()
        {
            Save();
            ScanApps();
            RefreshState();
            RefreshSettingsNotes();
        }

        void SaveTime()
        {
            try { timeLog.Save(App.TimePath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        string TimeLabel(string key) => TimeKeys.Label(key, settings.WatchApps);

        string TimeText()
        {
            string today = TimeLog.Summary(timeLog.Today, TimeLabel), total = TimeLog.Summary(timeLog.Total, TimeLabel);
            return "Today: " + (today.Length > 0 ? today : "nothing yet") + "\nAll time: " + (total.Length > 0 ? total : "nothing yet");
        }

        // ---- updates ----------------------------------------------------------------------

        void CheckForUpdates(bool manual)
        {
            if (checkingUpdate) return;
            checkingUpdate = true;
            if (manual) updateStatus = "Checking for updates";
            System.Threading.Tasks.Task.Run(() => Updater.Check()).ContinueWith(t => ui.Post(_ =>
            {
                checkingUpdate = false;
                var r = t.Result;
                string current = Updater.Current.ToString(3);
                if (r.Downloaded != null)
                {
                    pendingUpdate = r.Downloaded;
                    updateStatus = "Version " + r.Latest.ToString(3) + " installs when no agent is working";
                    TryApplyUpdate();
                }
                else if (r.Problem != null) updateStatus = "Update check failed: " + r.Problem;
                else
                {
                    updateStatus = "Up to date (" + current + ")";
                    if (manual) Enqueue(new ToastRequest { Kind = ToastKind.Info, Title = "Aevalsistant is up to date", Detail = "Version " + current + " is the latest release." });
                }
                RefreshSettingsNotes();
            }, null));
            RefreshSettingsNotes();
        }

        // Restarting forgets which sessions are working, so an update waits until none are.
        void TryApplyUpdate()
        {
            if (pendingUpdate == null || book.BusyCount > 0 || toast.Showing || !File.Exists(pendingUpdate)) return;
            string path = pendingUpdate;
            pendingUpdate = null;
            string problem = Updater.Apply(path);
            if (problem != null) updateStatus = "Could not start the update: " + problem;
        }

        void RefreshState()
        {
            busyWindows = new HashSet<long>(book.All.Where(x => x.Busy && x.Agent != "chat" && x.Hwnd != 0).Select(x => x.Hwnd));
            TryApplyUpdate();
            int working = book.BusyCount;
            bool on = working > 0 || appsOpen.Count > 0 || holds.Active;
            bool keep = on && settings.KeepAwake;
            awake.Set(keep, keep && settings.DisplayOn);

            bool wantLid = keep && settings.LidAwake && hasLid;
            if (wantLid)
            {
                lidSleep.Stop();
                lidActionDue = null;
                if (settings.LidSaved.Length == 0)
                {
                    string saved = LidAction.Override(out lidProblem);
                    if (saved != null) { settings.LidSaved = saved; Save(); }
                    RefreshSettingsNotes();
                }
            }
            else if (settings.LidSaved.Length > 0)
            {
                string saved = settings.LidSaved;
                if (LidAction.Restore(saved))
                {
                    settings.LidSaved = "";
                    Save();
                    if (!on && lid?.Closed == true)
                    {
                        lidActionDue = LidAction.SavedAction(saved, Battery.OnBattery());
                        lidSleep.Stop();
                        if (lidActionDue == 1 || lidActionDue == 2) lidSleep.Start();
                    }
                }
            }

            if (trayAwake != on)
            {
                trayAwake = on;
                var old = trayIcon;
                int px = SystemInformation.SmallIconSize.Width;
                using (var bmp = Theme.TrayGlyph(px, on)) trayIcon = Icon.FromHandle(bmp.GetHicon());
                tray.Icon = trayIcon;
                if (old != null) { Native.DestroyIcon(old.Handle); old.Dispose(); }
            }
            string tip = keep ? "Aevalsistant: keeping awake, " + AwakeReason(working)
                : on ? "Aevalsistant: " + AwakeReason(working) + ", sleep allowed"
                : "Aevalsistant: idle, sleep allowed";
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }

        // Sleep or hibernate, whichever closing the lid is set to do, if the lid is still shut
        // and nothing started working again in the meantime.
        void SleepIfLidStillShut()
        {
            lidSleep.Stop();
            uint? action = lidActionDue;
            lidActionDue = null;
            if (action == null || book.BusyCount > 0 || appsOpen.Count > 0 || holds.Active || lid?.Closed != true) return;
            Battery.Suspend(hibernate: action == 2);
        }

        // ---- menu ---------------------------------------------------------------------

        void ShowMenuAtCursor()
        {
            // NotifyIcon only positions its menu correctly through this internal method.
            typeof(NotifyIcon).GetMethod("ShowContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.Invoke(tray, null);
        }

        void BuildMenu()
        {
            foreach (ToolStripItem old in menu.Items) old.Image?.Dispose();
            menu.Items.Clear();
            float dpi = menu.DeviceDpi / 96f;
            int dot = (int)Math.Round(16 * dpi);
            menu.ImageScalingSize = new Size(dot, dot);

            int working = book.BusyCount;
            menu.Items.Add(Label("Aevalsistant " + Updater.Current.ToString(3), "header", bold: true));
            bool busy = working > 0 || appsOpen.Count > 0 || holds.Active;
            menu.Items.Add(Label(!busy ? "Idle. Sleep is allowed."
                : settings.KeepAwake ? "Keeping awake: " + AwakeReason(working)
                : AwakeReason(working) + ". Sleep is allowed.", "status"));
            menu.Items.Add(new ToolStripSeparator());

            // Every session, with what it started indented under it.
            const int MaxItems = 14;
            var tree = book.Tree(null, settings.ShowChildren);
            if (tree.Count == 0) menu.Items.Add(Label("No agent sessions yet", "status"));
            var now = DateTime.UtcNow;
            foreach (var r in tree.Take(MaxItems))
            {
                var row = RowFor(r, now);
                Color color = row.State == RowState.NeedsYou ? Theme.Blush : row.State == RowState.Working ? Theme.Slate : Theme.SlateSoft;
                string text = row.Host.Length > 0 ? row.Name + "  \u00B7  " + row.Host : row.Name;
                string worked = r.Subagent == null ? Status.Worked(r.Session) : "";
                var item = new ToolStripMenuItem(r.Depth > 0 ? "\u2003\u2003" + text : text)
                {
                    ShortcutKeyDisplayString = worked.Length > 0 ? row.Status + "  \u00B7  " + worked : row.Status,
                    Image = Theme.Dot(r.Depth > 0 ? (int)Math.Round(dot * 0.8) : dot, color, row.State == RowState.Working),
                    Enabled = CanFocus(row.SessionId, row.Hwnd),
                    ToolTipText = r.Subagent == null && r.Session.Cwd.Length > 0 ? r.Session.Cwd : row.Name,
                };
                string id = row.SessionId;
                long hwnd = row.Hwnd;
                item.Click += (o, e) => FocusTarget(id, hwnd);
                menu.Items.Add(item);
            }
            if (tree.Count > MaxItems) menu.Items.Add(Label((tree.Count - MaxItems) + " more", "status"));
            foreach (var kv in appsOpen.OrderBy(kv => kv.Value))
            {
                double secs = (now - kv.Value).TotalSeconds;
                var item = Label(AppLabel(kv.Key) + "  \u00B7  open", "app");
                item.ShortcutKeyDisplayString = secs < 60 ? "just opened" : Status.Duration(secs);
                item.Image = Theme.Dot(dot, Theme.Slate, true);
                menu.Items.Add(item);
            }
            foreach (var h in holds.All.ToList())
            {
                var item = new ToolStripMenuItem(h.Label + " keeps awake" + (h.Reason.Length > 0 ? "  \u00B7  " + h.Reason : ""))
                {
                    ShortcutKeyDisplayString = AwakeHolds.Text(h, now),
                    Image = Theme.Dot(dot, Theme.Slate, true),
                    ToolTipText = "Click to release",
                };
                string key = h.Key;
                item.Click += (o, e) => { holds.Release(key); RefreshState(); };
                menu.Items.Add(item);
            }
            string todaySummary = TimeLog.Summary(timeLog.Today, TimeLabel);
            if (todaySummary.Length > 0) menu.Items.Add(Label("Today: " + todaySummary, "status"));
            menu.Items.Add(new ToolStripSeparator());

            // Things that need doing stay visible here; everything else lives in Settings.
            foreach (var problem in agentProblems.Values) menu.Items.Add(Label(problem, "status"));
            if (lidProblem != null) menu.Items.Add(Label(lidProblem, "status"));
            if (chatProblem != null) menu.Items.Add(Label(chatProblem, "status"));
            if (justConnected.Count > 0) menu.Items.Add(Label("Restart open agent sessions once to connect them", "status"));
            if (pendingUpdate != null) menu.Items.Add(Label(updateStatus, "status"));
            if (powerKind != null) menu.Items.Add(Command("Cancel the " + powerKind, CancelPower));
            menu.Items.Add(Command("Settings", OpenSettings));
            menu.Items.Add(Command("Quit", Quit));
        }

        // Enabled so the renderer's colors apply (disabled items are forced to system gray);
        // the renderer skips their highlight and the menu ignores clicks on them.
        static ToolStripMenuItem Label(string text, string tag, bool bold = false)
        {
            var item = new ToolStripMenuItem(text) { Tag = tag };
            if (bold) item.Font = new Font(Theme.Family, 9.75f, FontStyle.Bold);
            return item;
        }

        static ToolStripMenuItem Command(string text, Action run)
        {
            var item = new ToolStripMenuItem(text);
            item.Click += (s, e) => run();
            return item;
        }

        // Shown even with finish cards turned off, since asking for it is the point. The long
        // message shows off the hover expansion.
        void TestToast()
        {
            var s = book.All.FirstOrDefault(x => CanFocus(x.Id, x.Hwnd));
            var req = new ToastRequest
            {
                SessionId = s?.Id, Hwnd = s?.Hwnd ?? 0, Host = s?.Where ?? "", Kind = ToastKind.Done,
                Title = s != null ? s.Project + " finished" : "Test notification",
                Detail = (s != null ? "Click this card, or press Alt+Tab while it is up, to jump to that window." : "Start an agent session to jump to it from here.")
                    + " Hover over a card to read its whole message: long replies drop down to several lines, and the card waits while the mouse is on it.",
            };
            if (req.SessionId != null) pending.RemoveAll(p => p.SessionId == req.SessionId);
            pending.Add(req);
            if (settings.Sound) Sound.Play(req.Kind);
            ShowTop();
        }

        // ---- settings window --------------------------------------------------------------

        // Options that only matter while another one is on sit under it and hide with it.
        void OpenSettings()
        {
            if (settingsWindow != null)
            {
                if (settingsWindow.WindowState == FormWindowState.Minimized) settingsWindow.WindowState = FormWindowState.Normal;
                settingsWindow.Activate();
                return;
            }
            var w = new SettingsWindow();

            w.Section("Keep awake");
            var keep = w.AddToggle("Keep the PC awake while agents work",
                "Windows won't sleep until the last agent stops. The screen can still turn off on its usual timer.",
                settings.KeepAwake, v => { settings.KeepAwake = v; Save(); RefreshState(); });
            w.AddToggle("Keep the screen on too", null, settings.DisplayOn, v => { settings.DisplayOn = v; Save(); RefreshState(); }, keep);
            if (hasLid)
            {
                var lidToggle = w.AddToggle("Keep working with the lid closed", "When the agents finish with the lid shut, the laptop goes to sleep.",
                    settings.LidAwake, v => { settings.LidAwake = v; lidProblem = null; Save(); RefreshState(); RefreshSettingsNotes(); }, keep);
                lidNote = w.AddNote(lidProblem ?? "", lidToggle);
            }
            watchToggle = w.AddToggle("Also while these apps are open",
                "Counts as an agent working: the PC stays awake, and the screen and lid settings apply.",
                settings.WatchAppsOn, v => { settings.WatchAppsOn = v; AppsChanged(); }, keep);
            appToggles.Clear();
            foreach (var app in settings.WatchApps.ToList())
            {
                var a = app;
                appToggles[a.Name] = w.AddToggle(a.Label, AppNote(a), a.On, v => SetAppOn(a, v), watchToggle);
            }
            w.AddButton("Add an app\u2026", OpenPicker, watchToggle);

            w.Section("Notifications");
            notifyChoice = w.AddChoice("Notify", new[] { "Always", "If needed", "Agent decides", "Never" }, settings.NotifyMode, i =>
            {
                settings.NotifyMode = i;
                Save();
                SyncGuide();
                if (doneToggle != null) doneToggle.On = settings.NotifyDone;
                if (asksToggle != null) asksToggle.On = settings.NotifyNeedsYou;
            });
            var done = doneToggle = w.AddToggle("Show a card when an agent finishes", null, settings.NotifyDone, v =>
            {
                settings.NotifyDone = v;
                if (v) settings.AgentDecides = false;
                Save();
                SyncGuide();
                if (notifyChoice != null) notifyChoice.Selected = settings.NotifyMode;
            });
            w.AddToggle("Also when background agents finish", "Subagents, and agents that another agent started.",
                settings.NotifyBackgroundDone, v => { settings.NotifyBackgroundDone = v; Save(); }, done);
            w.AddChoice("Only after turns longer than", new[] { "Any", "1 min", "5 min", "15 min" }, settings.MinTurn,
                i => { settings.MinTurn = i; Save(); }, done);
            var asks = asksToggle = w.AddToggle("Show a card when an agent needs you", null, settings.NotifyNeedsYou, v =>
            {
                settings.NotifyNeedsYou = v;
                Save();
                if (notifyChoice != null) notifyChoice.Selected = settings.NotifyMode;
            });
            w.AddToggle("Also when a background agent needs you", null,
                settings.NotifyBackgroundNeedsYou, v => { settings.NotifyBackgroundNeedsYou = v; Save(); }, asks);
            var remind = w.AddToggle("Remind me while an agent still needs you", null, settings.Remind, v => { settings.Remind = v; Save(); }, asks);
            w.AddChoice("Remind every", new[] { "2 min", "5 min", "10 min" }, settings.RemindAfter, i => { settings.RemindAfter = i; Save(); }, remind);
            w.AddToggle("Only when I'm not looking at the agent", "No card when the agent's window is already in front.",
                settings.NotifyAway, v => { settings.NotifyAway = v; Save(); });
            var sound = w.AddToggle("Play a sound", "A soft chime with each card. Quiet during presentations and full-screen games.",
                settings.Sound, v => { settings.Sound = v; Save(); });
            w.AddButton("Finished", () => Sound.Play(ToastKind.Done), sound);
            w.AddButton("Needs you", () => Sound.Play(ToastKind.NeedsYou), sound);
            w.AddToggle("Expand the card on hover to show the whole message", null,
                settings.ExpandOnHover, v => { settings.ExpandOnHover = v; toast.ExpandOnHover = v; Save(); });
            w.AddToggle("Alt+Tab jumps to the agent while the card is up", null, settings.AltTab, v => { settings.AltTab = v; Save(); });
            w.AddChoice("Card stays up", new[] { "Short", "Normal", "Long" }, settings.CardTime, i => { settings.CardTime = i; Save(); });
            w.AddButton("Show a test notification", TestToast);

            w.Section("Agents");
            var hooks = w.AddToggle("Connect coding agents", "Adds Aevalsistant to each agent's hook settings. Restart open sessions once after a change.",
                settings.Hooks, v => { settings.Hooks = v; Save(); SyncAgents(v); RefreshSettingsNotes(); });
            agentToggles.Clear();
            foreach (var a in Agents.All.Where(AgentPresent))
            {
                string id = a.Id;
                agentToggles[id] = w.AddToggle(a.Name, AgentStatus(a), !settings.AgentsOff.Contains(id), v =>
                {
                    if (v) settings.AgentsOff.Remove(id); else settings.AgentsOff.Add(id);
                    Save();
                    SyncAgents(settings.Hooks);
                    RefreshSettingsNotes();
                }, hooks);
            }
            w.AddToggle("List subagents under their session", "In the card and the tray menu.", settings.ShowChildren, v =>
            {
                settings.ShowChildren = v;
                Save();
                if (toast.Showing) ShowTop(false);
            });
            var chat = w.AddToggle("Watch Claude and ChatGPT desktop chats", "Notices a reply finishing while you are in another app.", settings.Chats, v =>
            {
                settings.Chats = v;
                chatProblem = null;
                Save();
                if (v) StartChats(); else StopChats();
                RefreshSettingsNotes();
            });
            chatNote = w.AddNote(chatProblem ?? "", chat);

            w.Section("Agent control");
            w.AddToggle("Let agents keep the PC awake", "An agent can run --awake 2h to hold the PC awake after its turn ends.",
                settings.AgentAwake, v => { settings.AgentAwake = v; Save(); SyncGuide(); });
            w.AddToggle("Let agents put the PC to sleep", null, settings.AgentSleep, v => { settings.AgentSleep = v; Save(); SyncGuide(); });
            w.AddToggle("Let agents restart or shut down the PC", "Refused while anything is busy, and you get 60 seconds to cancel.",
                settings.AgentPower, v => { settings.AgentPower = v; Save(); SyncGuide(); });
            w.AddToggle("Teach Claude Code these commands", "Adds a short section to ~/.claude/CLAUDE.md that follows these settings.",
                settings.AgentGuide, v => { settings.AgentGuide = v; Save(); SyncGuide(); });

            w.Section("Time");
            timeNote = w.AddNote(TimeText());
            w.AddButton("Reset times", () => { timeLog.Reset(); SaveTime(); RefreshSettingsNotes(); });

            w.Section("General");
            w.AddToggle("Start with Windows", null, settings.Startup, v => { settings.Startup = v; Save(); SetStartup(v); });
            w.AddToggle("Update automatically", "Checks this app's GitHub releases two minutes after start and every six hours.",
                settings.AutoUpdate, v => { settings.AutoUpdate = v; Save(); });
            w.AddButton("Check for updates", () => CheckForUpdates(manual: true));
            updateNote = w.AddNote(UpdateLine());
            w.AddLink("Release notes", "https://github.com/" + Updater.Repo + "/releases");
            w.AddLink("Report a problem", "https://github.com/" + Updater.Repo + "/issues/new/choose");
            w.AddButton("Remove from this PC", Uninstall, danger: true);

            w.FormClosed += (s, e) =>
            {
                settingsWindow = null;
                updateNote = lidNote = chatNote = timeNote = null;
                watchToggle = doneToggle = asksToggle = null;
                notifyChoice = null;
                agentToggles.Clear();
                appToggles.Clear();
                picker?.Close();
            };
            settingsWindow = w;
            w.Show();
            w.Activate();
        }

        string UpdateLine() => updateStatus ?? "Version " + Updater.Current.ToString(3);

        string AgentStatus(AgentSpec a)
        {
            if (agentProblems.TryGetValue(a.Id, out var problem)) return problem;
            if (!settings.Hooks || settings.AgentsOff.Contains(a.Id)) return "Not connected";
            if (!AgentConnected(a)) return "Not connected yet";
            return justConnected.Contains(a.Id) && a.Note.Length > 0 ? "Connected. " + a.Note : "Connected";
        }

        // Status lines in an open Settings window follow what happens elsewhere.
        void RefreshSettingsNotes()
        {
            if (settingsWindow == null) return;
            if (updateNote != null) updateNote.Text = UpdateLine();
            if (lidNote != null) lidNote.Text = lidProblem ?? "";
            if (chatNote != null) chatNote.Text = chatProblem ?? "";
            foreach (var a in Agents.All)
                if (agentToggles.TryGetValue(a.Id, out var t)) t.Note = AgentStatus(a);
            foreach (var app in settings.WatchApps)
                if (appToggles.TryGetValue(app.Name, out var t)) t.Note = AppNote(app);
            if (timeNote != null) timeNote.Text = TimeText();
        }

        // ---- install surface ----------------------------------------------------------

        static string HookCommand() => Agents.HookPath(App.InstalledExe, path =>
        {
            var sb = new StringBuilder(520);
            uint n = Native.GetShortPathName(path, sb, (uint)sb.Capacity);
            return n > 0 && n < sb.Capacity ? sb.ToString() : null;
        }) + " --hook";

        static string AgentFile(AgentSpec a) =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), a.File);

        // Installed means the tool's config folder exists. Claude Code is the exception: its
        // folder is created if missing, since it is the main reason this app exists.
        static bool AgentPresent(AgentSpec a) =>
            a.Id == "claude" || Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), a.Dir));

        bool AgentConnected(AgentSpec a)
        {
            try
            {
                string path = AgentFile(a);
                return File.Exists(path) && Agents.IsInstalled(a, File.ReadAllText(path), Agents.CommandFor(a, hookCommand));
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        // Adds our hooks to every coding agent found on this PC, or removes them from all.
        // Each file is backed up once (name.aevalsistant.bak) before its first change.
        void SyncAgents(bool install)
        {
            foreach (var a in Agents.All)
            {
                string path = AgentFile(a);
                string shown = "~/" + a.File.Replace('\\', '/');
                try
                {
                    string text = File.Exists(path) ? File.ReadAllText(path) : null;
                    bool add = install && AgentPresent(a) && !settings.AgentsOff.Contains(a.Id);
                    if (text == null && !add) { agentProblems.Remove(a.Id); continue; }
                    string next = Agents.Apply(a, text, add ? Agents.CommandFor(a, hookCommand) : null, out bool changed);
                    if (changed)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        string backup = path + ".aevalsistant.bak";
                        if (a.Layout != HookLayout.Own && text != null && !File.Exists(backup)) File.WriteAllText(backup, text);
                        if (next == null) File.Delete(path);
                        else WriteConfig(path, next);
                        if (add) justConnected.Add(a.Id);
                    }
                    agentProblems.Remove(a.Id);
                }
                catch (FormatException) { agentProblems[a.Id] = a.Name + ": " + shown + " is not plain JSON; hooks not added"; }
                catch (IOException e) { agentProblems[a.Id] = a.Name + ": could not write " + shown + " (" + e.Message + ")"; }
                catch (UnauthorizedAccessException) { agentProblems[a.Id] = a.Name + ": no permission to write " + shown; }
            }
        }

        // Written next to the original and swapped in, so a crash or power cut mid-write cannot
        // leave an agent's settings half written. A symlink (dotfile managers) is written in
        // place instead, so it stays a symlink.
        static void WriteConfig(string path, string text)
        {
            var utf8 = new UTF8Encoding(false);
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                File.WriteAllText(path, text, utf8);
                return;
            }
            string temp = path + ".aevalsistant.tmp";
            try
            {
                File.WriteAllText(temp, text, utf8);
                File.Replace(temp, path, null);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        static void SetStartup(bool on)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) key.SetValue("Aevalsistant", "\"" + App.InstalledExe + "\"");
                else if (key.GetValue("Aevalsistant") != null) key.DeleteValue("Aevalsistant");
            }
        }

        void Uninstall()
        {
            var answer = MessageBox.Show(settingsWindow,
                "Remove Aevalsistant? Its hooks in every coding agent, its startup entry, and its folder in AppData will be deleted.\n\n"
                + "Restart open agent sessions afterwards so they stop calling it.",
                "Aevalsistant", MessageBoxButtons.OKCancel, MessageBoxIcon.None, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.OK) return;
            SyncAgents(false);
            SyncGuide(remove: true);
            SetStartup(false);
            // The exe cannot delete its own folder while running. cmd tries once a second for up
            // to 15 seconds, since a --hook call or a virus scan can hold the exe after this exits.
            string home = App.Home;
            Process.Start(new ProcessStartInfo("cmd.exe",
                "/c for /l %i in (1,1,15) do (ping -n 2 127.0.0.1 >nul & rmdir /s /q \"" + home + "\" 2>nul & if not exist \"" + home + "\\\" exit)")
            {
                CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath(),
            });
            Quit();
        }

        void Save() => settings.Save(App.SettingsPath);

        void Quit()
        {
            if (quitting) return;
            quitting = true;
            sweep.Stop();
            updateTimer.Stop();
            if (settings.LidSaved.Length > 0 && LidAction.Restore(settings.LidSaved)) { settings.LidSaved = ""; }
            if (Directory.Exists(App.Home)) { Save(); SaveTime(); }
            settingsWindow?.Close();
            lidSleep.Dispose();
            powerTimer.Dispose();
            lid?.Dispose();
            chats?.Dispose();
            awake.Dispose();
            tray.Visible = false;
            tray.Dispose();
            toast.Dispose();
            ExitThread();
        }
    }
}

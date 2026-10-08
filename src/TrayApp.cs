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

        public TrayApp()
        {
            if (!(SynchronizationContext.Current is WindowsFormsSynchronizationContext))
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            ui = SynchronizationContext.Current;

            toast = new ToastWindow();
            toast.Clicked += Activate;
            toast.AltTabbed += () => Activate(ToastArt.HitHead);
            toast.Expired += () => { pending.Clear(); toast.Hide(); };
            toast.Dismissed += () => { pending.Clear(); toast.Hide(); };

            // Repair a lid override left behind by a crash or power loss.
            if (settings.LidSaved.Length > 0 && LidAction.Restore(settings.LidSaved)) { settings.LidSaved = ""; Save(); }

            if (settings.Hooks) SyncAgents(true);
            if (settings.Chats) StartChats();
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

            Updater.CleanUp();
            updateTimer.Tick += (s, e) =>
            {
                updateTimer.Interval = 6 * 60 * 60 * 1000;   // first check two minutes after start, then every six hours
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
                    Detail = "Restart open Claude Code sessions once so they report here.",
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

        void FocusTarget(string sessionId, long hwnd)
        {
            long h = book.Get(sessionId)?.Hwnd ?? hwnd;
            if (h != 0) Terminals.Focus(new IntPtr(h));
        }

        // ---- toasts -------------------------------------------------------------------

        void Enqueue(ToastRequest req)
        {
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
                    if (pending.Contains(req)) ShowTop();
                };
                t.Start();
                return;
            }
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
                Keycap = CanFocus(top.SessionId, top.Hwnd),
            };
            var others = book.All.Where(x => x.Id != top.SessionId)
                .OrderBy(x => Status.State(x) == RowState.NeedsYou ? 0 : Status.State(x) == RowState.Done ? 1 : 2)
                .ThenByDescending(x => x.LastEvent)
                .ToList();
            var now = DateTime.UtcNow;
            foreach (var x in others.Take(ToastArt.MaxRows))
            {
                c.Rows.Add(new ToastRow
                {
                    SessionId = x.Id, Hwnd = x.Hwnd, Name = x.Project, Host = x.Where,
                    State = Status.State(x),
                    Status = Status.Text(x, now),
                });
            }
            if (others.Count > ToastArt.MaxRows)
                c.Overflow = (others.Count - ToastArt.MaxRows) + " more in the tray menu";

            shown = c;
            double seconds = !restartCountdown ? -1 : top.Kind == ToastKind.NeedsYou ? 10 : top.Kind == ToastKind.Info ? 5 : 8;
            toast.Show(c, seconds);
        }

        void Withdraw(string sessionId)
        {
            if (pending.RemoveAll(p => p.SessionId == sessionId) == 0) return;
            if (pending.Count == 0) toast.Hide();
            else if (toast.Showing) ShowTop(false);
        }

        // Click on the main row or Alt+Tab: that session. Click on a listed row: that one.
        void Activate(int target)
        {
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
            chats.Failed += () => { chats = null; chatProblem = "Chat watching is unavailable: Windows UI Automation did not load"; };
        }

        void StopChats()
        {
            chats?.Dispose();
            chats = null;
            foreach (var s in book.All.Where(x => x.Agent == "chat").ToList())
                Ingest(new HookEvent { Name = "SessionEnd", SessionId = s.Id });
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
            }, null));
        }

        // Restarting forgets which sessions are working, so an update waits until none are.
        void TryApplyUpdate()
        {
            if (pendingUpdate == null || book.BusyCount > 0 || toast.Showing || !File.Exists(pendingUpdate)) return;
            string path = pendingUpdate;
            pendingUpdate = null;
            Updater.Apply(path);
        }

        void RefreshState()
        {
            busyWindows = new HashSet<long>(book.All.Where(x => x.Busy && x.Agent != "chat" && x.Hwnd != 0).Select(x => x.Hwnd));
            TryApplyUpdate();
            int working = book.BusyCount;
            bool on = working > 0;
            awake.Set(on, on && settings.DisplayOn);

            bool wantLid = on && settings.LidAwake;
            if (wantLid && settings.LidSaved.Length == 0)
            {
                string saved = LidAction.Override(out lidProblem);
                if (saved != null) { settings.LidSaved = saved; Save(); }
            }
            else if (!wantLid && settings.LidSaved.Length > 0)
            {
                if (LidAction.Restore(settings.LidSaved)) { settings.LidSaved = ""; Save(); }
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
            string tip = on ? "Aevalsistant: keeping awake, " + Plural.Agents(working) + " working" : "Aevalsistant: idle, sleep allowed";
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
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
            menu.Items.Add(Label("Aevalsistant", "header", bold: true));
            menu.Items.Add(Label(working > 0 ? "Keeping awake: " + Plural.Agents(working) + " working" : "Idle. Sleep is allowed.", "status"));
            menu.Items.Add(new ToolStripSeparator());

            var sessions = book.All.Take(8).ToList();
            if (sessions.Count == 0) menu.Items.Add(Label("No Claude Code sessions yet", "status"));
            foreach (var s in sessions)
            {
                var rs = Status.State(s);
                string state = Status.Text(s, DateTime.UtcNow);
                Color color = rs == RowState.NeedsYou ? Theme.Blush : rs == RowState.Working ? Theme.Slate : Theme.SlateSoft;
                var item = new ToolStripMenuItem(s.Where.Length > 0 ? s.Project + "  \u00B7  " + s.Where : s.Project)
                {
                    ShortcutKeyDisplayString = state,
                    Image = Theme.Dot(dot, color, rs == RowState.Working),
                    Enabled = CanFocus(s.Id, s.Hwnd),
                    ToolTipText = s.Cwd.Length > 0 ? s.Cwd : s.Project,
                };
                string id = s.Id;
                long hwnd = s.Hwnd;
                item.Click += (o, e) => FocusTarget(id, hwnd);
                menu.Items.Add(item);
            }
            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add(Toggle("Stay awake with the lid closed", settings.LidAwake, v => { settings.LidAwake = v; lidProblem = null; Save(); RefreshState(); }));
            if (lidProblem != null) menu.Items.Add(Label(lidProblem, "status"));
            menu.Items.Add(Toggle("Keep the screen on while agents work", settings.DisplayOn, v => { settings.DisplayOn = v; Save(); RefreshState(); }));
            menu.Items.Add(Toggle("Start with Windows", settings.Startup, v => { settings.Startup = v; Save(); SetStartup(v); }));

            var connected = Agents.All.Where(AgentConnected).ToList();
            menu.Items.Add(Toggle("Connect coding agents", settings.Hooks && connected.Count > 0, v => { settings.Hooks = v; Save(); SyncAgents(v); }));
            if (settings.Hooks && connected.Count > 0)
                menu.Items.Add(Label("Connected: " + string.Join(", ", connected.Select(a => a.Name)), "status"));
            foreach (var problem in agentProblems.Values) menu.Items.Add(Label(problem, "status"));
            menu.Items.Add(Toggle("Watch Claude and ChatGPT chats", settings.Chats, v =>
            {
                settings.Chats = v;
                Save();
                if (v) StartChats(); else StopChats();
            }));
            if (chatProblem != null) menu.Items.Add(Label(chatProblem, "status"));
            if (justConnected.Count > 0) menu.Items.Add(Label("Restart open agent sessions once to connect them", "status"));
            foreach (var a in connected.Where(a => justConnected.Contains(a.Id) && a.Note.Length > 0))
                menu.Items.Add(Label(a.Note, "status"));
            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add(Toggle("Update automatically", settings.AutoUpdate, v => { settings.AutoUpdate = v; Save(); }));
            menu.Items.Add(Command("Check for updates", () => CheckForUpdates(manual: true)));
            if (updateStatus != null) menu.Items.Add(Label(updateStatus, "status"));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Command("Show a test notification", TestToast));
            menu.Items.Add(Command("Remove from this PC", Uninstall));
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

        static ToolStripMenuItem Toggle(string text, bool on, Action<bool> set)
        {
            var item = new ToolStripMenuItem(text) { Checked = on };
            item.Click += (s, e) => set(!on);
            return item;
        }

        static ToolStripMenuItem Command(string text, Action run)
        {
            var item = new ToolStripMenuItem(text);
            item.Click += (s, e) => run();
            return item;
        }

        void TestToast()
        {
            var s = book.All.FirstOrDefault(x => CanFocus(x.Id, x.Hwnd));
            Enqueue(new ToastRequest
            {
                SessionId = s?.Id, Hwnd = s?.Hwnd ?? 0, Host = s?.Where ?? "", Kind = ToastKind.Done,
                Title = s != null ? s.Project + " finished" : "Test notification",
                Detail = s != null ? "Click it, or press Alt+Tab, to jump to that window." : "Start a Claude Code session to jump to it from here.",
            });
        }

        // ---- install surface ----------------------------------------------------------

        static string HookCommand()
        {
            string path = App.InstalledExe;
            if (path.IndexOf(' ') >= 0)
            {
                // Hook commands run through whichever shell Claude Code uses; an 8.3 path with
                // forward slashes needs no quoting in bash, cmd, or PowerShell.
                var sb = new StringBuilder(520);
                if (Native.GetShortPathName(path, sb, (uint)sb.Capacity) > 0) path = sb.ToString();
            }
            path = path.Replace('\\', '/');
            return (path.IndexOf(' ') >= 0 ? "\"" + path + "\"" : path) + " --hook";
        }

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
                    bool add = install && AgentPresent(a);
                    if (text == null && !add) { agentProblems.Remove(a.Id); continue; }
                    string next = Agents.Apply(a, text, add ? Agents.CommandFor(a, hookCommand) : null, out bool changed);
                    if (changed)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        string backup = path + ".aevalsistant.bak";
                        if (a.Layout != HookLayout.Own && text != null && !File.Exists(backup)) File.WriteAllText(backup, text);
                        if (next == null) File.Delete(path);
                        // Written in place rather than swapped in, so a symlinked file (dotfile
                        // managers) stays a symlink.
                        else File.WriteAllText(path, next, new UTF8Encoding(false));
                        if (add) justConnected.Add(a.Id);
                    }
                    agentProblems.Remove(a.Id);
                }
                catch (FormatException) { agentProblems[a.Id] = a.Name + ": " + shown + " is not plain JSON; hooks not added"; }
                catch (IOException e) { agentProblems[a.Id] = a.Name + ": could not write " + shown + " (" + e.Message + ")"; }
                catch (UnauthorizedAccessException) { agentProblems[a.Id] = a.Name + ": no permission to write " + shown; }
            }
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
            var answer = MessageBox.Show(
                "Remove Aevalsistant? Its Claude Code hooks, its startup entry, and its folder in AppData will be deleted.",
                "Aevalsistant", MessageBoxButtons.OKCancel, MessageBoxIcon.None, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.OK) return;
            SyncAgents(false);
            SetStartup(false);
            // The exe cannot delete its own folder while running; cmd does it two seconds after exit.
            Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"" + App.Home + "\"")
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
            if (Directory.Exists(App.Home)) Save();
            chats?.Dispose();
            awake.Dispose();
            tray.Visible = false;
            tray.Dispose();
            toast.Dispose();
            ExitThread();
        }
    }
}

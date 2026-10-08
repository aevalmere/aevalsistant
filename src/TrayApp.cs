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

        void FocusTarget(string sessionId, long hwnd)
        {
            long h = book.Get(sessionId)?.Hwnd ?? hwnd;
            if (h != 0) Terminals.Focus(new IntPtr(h));
        }

        // ---- toasts -------------------------------------------------------------------

        void Enqueue(ToastRequest req)
        {
            if (!settings.Wants(req))
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
            toast.Show(c, restartCountdown ? settings.Seconds(top.Kind) : -1);
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
            bool on = working > 0;
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
            string tip = keep ? "Aevalsistant: keeping awake, " + Plural.Agents(working) + " working"
                : on ? "Aevalsistant: " + Plural.Agents(working) + " working, sleep allowed"
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
            if (action == null || book.BusyCount > 0 || lid?.Closed != true) return;
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
            menu.Items.Add(Label(working == 0 ? "Idle. Sleep is allowed."
                : settings.KeepAwake ? "Keeping awake: " + Plural.Agents(working) + " working"
                : Plural.Agents(working) + " working. Sleep is allowed.", "status"));
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
                var item = new ToolStripMenuItem(r.Depth > 0 ? "\u2003\u2003" + text : text)
                {
                    ShortcutKeyDisplayString = row.Status,
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
            menu.Items.Add(new ToolStripSeparator());

            // Things that need doing stay visible here; everything else lives in Settings.
            foreach (var problem in agentProblems.Values) menu.Items.Add(Label(problem, "status"));
            if (lidProblem != null) menu.Items.Add(Label(lidProblem, "status"));
            if (chatProblem != null) menu.Items.Add(Label(chatProblem, "status"));
            if (justConnected.Count > 0) menu.Items.Add(Label("Restart open agent sessions once to connect them", "status"));
            if (pendingUpdate != null) menu.Items.Add(Label(updateStatus, "status"));
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

            w.Section("Notifications");
            var done = w.AddToggle("Show a card when an agent finishes", null, settings.NotifyDone, v => { settings.NotifyDone = v; Save(); });
            w.AddToggle("Also when background agents finish", "Subagents, and agents that another agent started.",
                settings.NotifyBackgroundDone, v => { settings.NotifyBackgroundDone = v; Save(); }, done);
            var asks = w.AddToggle("Show a card when an agent needs you", null, settings.NotifyNeedsYou, v => { settings.NotifyNeedsYou = v; Save(); });
            w.AddToggle("Also when a background agent needs you", null,
                settings.NotifyBackgroundNeedsYou, v => { settings.NotifyBackgroundNeedsYou = v; Save(); }, asks);
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
                updateNote = lidNote = chatNote = null;
                agentToggles.Clear();
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
            if (Directory.Exists(App.Home)) Save();
            settingsWindow?.Close();
            lidSleep.Dispose();
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

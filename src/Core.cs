using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Aevalsistant
{
    enum AgentState { Working, Waiting }
    enum ToastKind { Done, NeedsYou, Info }
    enum RowState { Working, Done, NeedsYou }

    // A subagent that started and has not stopped. Background subagents keep running after the
    // parent's turn ends.
    sealed class Subagent
    {
        public string Type = "";   // "Explore", "general-purpose"...; empty when the agent does not say
        public DateTime Started;
        public DateTime Seen;      // last sign of life: its start, or a write to its transcript
    }

    sealed class Session
    {
        public string Id;
        public string Cwd = "";
        public string Transcript = "";
        public long Hwnd;
        public int Pid;
        public long PidStart;
        // The agent process that started this session's agent, when one did: a Claude Code
        // session running `claude -p` or `codex exec` in its shell. Matched against other
        // sessions' Pid to nest this one under its parent.
        public int OuterPid;
        public long OuterStart;
        public AgentState State = AgentState.Waiting;
        public bool Blocked;              // a permission prompt is open
        public bool NotifiedSincePrompt;
        public DateTime LastEvent;
        public DateTime LastActivity;     // newest of hook events and transcript writes
        public DateTime TranscriptSeen;   // transcript mtime at the last sweep
        public long PromptAt, StopAt;     // hook process start ticks, to order async deliveries
        public string Host = "";          // the app hosting it: "VS Code", "Android Studio", "Claude"...
        public string Agent = "claude";   // which coding agent: claude, codex, gemini, copilot, cursor, windsurf, opencode
        public string Label = "";         // fixed name for sessions without a folder (chat apps)
        public string PendingText = "";   // the agent's last reply, for agents that send it before Stop
        public string Where => Agents.Where(Agent, Host);
        public DateTime WorkingSince;     // when the current turn started
        public double Worked;             // seconds spent busy since this app first saw the session
        public DateTime BlockedSince, RemindedAt;   // when the open permission prompt appeared, and the last reminder for it
        public readonly Dictionary<string, Subagent> Subagents = new Dictionary<string, Subagent>();
        readonly HashSet<string> stoppedEarly = new HashSet<string>();   // async SubagentStop that beat its Start
        public bool Busy => State == AgentState.Working || Subagents.Count > 0;

        // A subagent's own transcript: <session transcript minus .jsonl>/subagents/agent-<id>.jsonl
        public string SubagentTranscript(string agentId) =>
            Transcript.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) && agentId.Length > 0
                ? Path.Combine(Transcript.Substring(0, Transcript.Length - 6), "subagents", "agent-" + agentId + ".jsonl")
                : "";

        public void SubagentStarted(string id, string type, DateTime now)
        {
            if (!stoppedEarly.Remove(id)) Subagents[id] = new Subagent { Type = type ?? "", Started = now, Seen = now };
        }

        // True when this was the last one running.
        public bool SubagentStopped(string id)
        {
            if (Subagents.Remove(id)) return Subagents.Count == 0;
            stoppedEarly.Add(id);
            return false;
        }

        // Last folder of the working directory. Split by hand so Windows paths behave the
        // same wherever this runs.
        public string Project
        {
            get
            {
                if (!string.IsNullOrEmpty(Label)) return Label;
                string trimmed = (Cwd ?? "").TrimEnd('\\', '/');
                if (trimmed.Length == 0) return Agents.Get(Agent).Name;
                int cut = trimmed.LastIndexOfAny(new[] { '\\', '/' });
                return cut < 0 ? trimmed : trimmed.Substring(cut + 1);
            }
        }
    }

    sealed class HookEvent
    {
        public string Name = "";
        public string SessionId = "";
        public string Cwd = "";
        public string Transcript = "";
        public string Message = "";
        public string NotificationType = "";
        public string LastAssistant = "";
        public long Hwnd;
        public int Pid;
        public long PidStart;
        public long At;   // when the hook process started; async hooks can arrive out of order
        public string Host = "";
        public string AgentId = "";
        public string AgentType = "";   // a subagent's kind, from SubagentStart
        public string Agent = "claude";
        public string Label = "";
        public bool CwdGuessed;
        public int OuterPid;
        public long OuterStart;

        // The envelope the --hook client sends:
        // {"t":..,"agent":"codex","event":"","pwd":"..","host":"..","hwnd":..,"pid":..,"pidStart":..,"outer":..,"outerStart":..,"raw":"<stdin>"}
        public static HookEvent FromEnvelope(string text)
        {
            if (!Json.TryParse(text, out var env) || !(env is JObj o)) return null;
            if (!(o["raw"] is string raw) || !Json.TryParse(raw, out var inner) || !(inner is JObj h)) return null;
            var e = new HookEvent
            {
                Agent = string.IsNullOrEmpty(o.Str("agent")) ? "claude" : o.Str("agent"),
                Name = o.Str("event") ?? "",
                Hwnd = (o["hwnd"] as JNum)?.AsLong() ?? 0,
                Pid = (int)((o["pid"] as JNum)?.AsLong() ?? 0),
                PidStart = (o["pidStart"] as JNum)?.AsLong() ?? 0,
                At = (o["t"] as JNum)?.AsLong() ?? 0,
                Host = o.Str("host") ?? "",
                OuterPid = (int)((o["outer"] as JNum)?.AsLong() ?? 0),
                OuterStart = (o["outerStart"] as JNum)?.AsLong() ?? 0,
            };
            Agents.Normalize(e.Agent, h, e, o.Str("pwd"));
            return e.SessionId.Length == 0 || e.Name.Length == 0 ? null : e;
        }
    }

    sealed class ToastRequest
    {
        public string SessionId;   // null for app messages
        public long Hwnd;
        public string Host = "";   // shown after the title so you know which window it is
        public ToastKind Kind;
        public string Title;
        public string Detail;
        public bool WantsSnippet;  // fill Detail from the transcript just before showing
        // Came from work running in the background rather than from your own prompt: a session
        // that another agent started, a turn the agent took on its own after a background
        // subagent reported back, or the last background subagent finishing. Settings decide
        // whether these show a card.
        public bool Background;
        public double TurnSeconds;   // how long the turn that just ended took, when known
    }

    sealed class SessionBook
    {
        public static readonly TimeSpan StaleWorking = TimeSpan.FromMinutes(45);
        public static readonly TimeSpan ForgetUntracked = TimeSpan.FromHours(12);

        readonly Dictionary<string, Session> map = new Dictionary<string, Session>();

        public IEnumerable<Session> All => map.Values.OrderByDescending(s => s.LastEvent);
        public int BusyCount => map.Values.Count(s => s.Busy);
        public Session Get(string id) => id != null && map.TryGetValue(id, out var s) ? s : null;

        // Sessions in display order, needs-you first, then done, then working. With nest on, each
        // is followed by what it started: its subagents, then sessions its agent started (and
        // theirs), one level in. headId is left out of the list because the card shows it above,
        // but its own children lead the list.
        public List<TreeRow> Tree(string headId, bool nest)
        {
            int Rank(Session s) => Status.State(s) == RowState.NeedsYou ? 0 : Status.State(s) == RowState.Done ? 1 : 2;
            Session Root(Session s)
            {
                for (int hops = 0; hops < 8; hops++) { var p = ParentOf(s); if (p == null) break; s = p; }
                return s;
            }
            var ordered = map.Values.OrderBy(Rank).ThenByDescending(s => s.LastEvent).ToList();
            var rows = new List<TreeRow>();
            void Children(Session parent)
            {
                foreach (var kv in parent.Subagents.OrderBy(kv => kv.Value.Started))
                    rows.Add(new TreeRow { Session = parent, SubagentId = kv.Key, Subagent = kv.Value, Depth = 1 });
                foreach (var c in ordered)
                    if (c != parent && c.Id != headId && Root(c) == parent)
                        rows.Add(new TreeRow { Session = c, Depth = 1 });
            }
            var head = Get(headId);
            if (nest && head != null) Children(head);
            foreach (var s in ordered)
            {
                if (s.Id == headId || (nest && Root(s) != s)) continue;
                rows.Add(new TreeRow { Session = s });
                if (nest) Children(s);
            }
            return rows;
        }

        // The session whose agent process started this one's, if this app knows it.
        public Session ParentOf(Session s) =>
            s == null || s.OuterPid == 0 ? null
            : map.Values.FirstOrDefault(p => p != s && p.Pid == s.OuterPid && (p.PidStart == 0 || s.OuterStart == 0 || p.PidStart == s.OuterStart));

        public ToastRequest Apply(HookEvent e, DateTime now)
        {
            if (e.Name == "SessionEnd") { map.Remove(e.SessionId); return null; }

            if (!map.TryGetValue(e.SessionId, out var s))
            {
                s = new Session { Id = e.SessionId };
                map[e.SessionId] = s;
            }
            if (e.Cwd.Length > 0 && (!e.CwdGuessed || s.Cwd.Length == 0)) s.Cwd = e.Cwd;
            if (e.Transcript.Length > 0) s.Transcript = e.Transcript;
            if (e.Hwnd != 0) s.Hwnd = e.Hwnd;
            if (e.Host.Length > 0) s.Host = e.Host;
            if (e.Agent.Length > 0) s.Agent = e.Agent;
            if (e.Label.Length > 0) s.Label = e.Label;
            if (e.Pid != 0) { s.Pid = e.Pid; s.PidStart = e.PidStart; }
            if (e.OuterPid != 0) { s.OuterPid = e.OuterPid; s.OuterStart = e.OuterStart; }
            s.LastEvent = now;
            s.LastActivity = now;
            bool child = ParentOf(s) != null;

            bool isPrompt = e.Name == "UserPromptSubmit";
            bool isStop = e.Name == "Stop" || e.Name == "StopFailure" || e.Name == "Interrupt";
            if (e.At != 0)
            {
                // A late UserPromptSubmit from before the latest Stop (or the reverse) is stale.
                if (isPrompt && e.At < s.StopAt) return null;
                if (isStop && e.At < s.PromptAt) return null;
                if (isPrompt) s.PromptAt = e.At;
                if (isStop) s.StopAt = e.At;
            }

            switch (e.Name)
            {
                case "AgentText":
                    s.PendingText = e.LastAssistant;
                    return null;

                case "Interrupt":
                    // You stopped it yourself, so there is nothing to tell you.
                    s.State = AgentState.Waiting;
                    s.Blocked = false;
                    s.NotifiedSincePrompt = true;
                    return null;

                case "SubagentStart":
                    s.SubagentStarted(e.AgentId.Length > 0 ? e.AgentId : "unnamed", e.AgentType, now);
                    return null;

                case "SubagentStop":
                    // Subagents that finish mid-turn are part of that turn. The last background
                    // one finishing after the turn ended is the end of everything you started.
                    if (!s.SubagentStopped(e.AgentId.Length > 0 ? e.AgentId : "unnamed") || s.State == AgentState.Working) return null;
                    return new ToastRequest
                    {
                        SessionId = s.Id, Hwnd = s.Hwnd, Host = s.Where, Kind = ToastKind.Done, Background = true,
                        Title = s.Project + " finished",
                        Detail = "Its background agents are done.",
                    };

                case "UserPromptSubmit":
                    if (s.State != AgentState.Working) s.WorkingSince = now;
                    s.State = AgentState.Working;
                    s.Blocked = false;
                    s.NotifiedSincePrompt = false;
                    s.PendingText = "";
                    return null;

                case "Stop":
                    // A second Stop with no prompt in between is a turn the agent took on its
                    // own, usually because a background subagent reported back.
                    bool onItsOwn = s.NotifiedSincePrompt;
                    s.State = AgentState.Waiting;
                    s.Blocked = false;
                    s.NotifiedSincePrompt = true;
                    string reply = e.LastAssistant.Length > 0 ? e.LastAssistant : s.PendingText;
                    s.PendingText = "";
                    return new ToastRequest
                    {
                        SessionId = s.Id, Hwnd = s.Hwnd, Host = s.Where, Kind = ToastKind.Done, Background = child || onItsOwn, TurnSeconds = TurnLength(s, now),
                        Title = s.Project + (s.Subagents.Count > 0 ? " is waiting" : " finished"),
                        Detail = reply.Length > 0 || s.Agent == "claude" ? Snippet.Clean(reply) : "Ready for your next prompt.",
                        // Only Claude Code's transcript format is known well enough to read the reply from.
                        WantsSnippet = reply.Length == 0 && s.Agent == "claude",
                    };

                case "StopFailure":
                    s.State = AgentState.Waiting;
                    s.Blocked = false;
                    s.NotifiedSincePrompt = true;
                    return new ToastRequest
                    {
                        SessionId = s.Id, Hwnd = s.Hwnd, Host = s.Where, Kind = ToastKind.NeedsYou, Background = child,
                        Title = s.Project + " stopped early",
                        Detail = "The turn ended on an error. The terminal has the details.",
                    };

                case "Notification":
                    string type = e.NotificationType.Length > 0 ? e.NotificationType : GuessType(e.Message);
                    if (type == "idle_prompt")
                    {
                        s.State = AgentState.Waiting;
                        s.Blocked = false;
                        if (s.NotifiedSincePrompt) return null;
                        s.NotifiedSincePrompt = true;
                        return new ToastRequest
                        {
                            SessionId = s.Id, Hwnd = s.Hwnd, Host = s.Where, Kind = ToastKind.Done, Background = child, TurnSeconds = TurnLength(s, now),
                            Title = s.Project + " is waiting",
                            Detail = e.Message.Length > 0 ? e.Message : "Ready for your next prompt.",
                        };
                    }
                    if (type == "permission_prompt" || type == "elicitation_dialog" || type == "elicitation_url_dialog" || type == "agent_needs_input")
                    {
                        // Still mid-turn: keep the machine awake, but ask for the human.
                        if (!s.Blocked) { s.BlockedSince = now; s.RemindedAt = DateTime.MinValue; }
                        s.Blocked = true;
                        return new ToastRequest
                        {
                            SessionId = s.Id, Hwnd = s.Hwnd, Host = s.Where, Kind = ToastKind.NeedsYou, Background = child,
                            Title = s.Project + " needs you",
                            Detail = e.Message.Length > 0 ? e.Message : "Claude is asking for permission.",
                        };
                    }
                    return null;
            }
            return null;
        }

        static double TurnLength(Session s, DateTime now) =>
            s.WorkingSince > DateTime.MinValue ? Math.Max(0, (now - s.WorkingSince).TotalSeconds) : 0;

        // Adds time to every busy session's total.
        public void Credit(double seconds)
        {
            foreach (var s in map.Values) if (s.Busy) s.Worked += seconds;
        }

        // A card again for each session still waiting on a permission prompt, once per interval.
        public List<ToastRequest> Reminders(DateTime now, TimeSpan every)
        {
            var list = new List<ToastRequest>();
            foreach (var s in All.ToList())
            {
                if (!s.Blocked) continue;
                var last = s.RemindedAt > s.BlockedSince ? s.RemindedAt : s.BlockedSince;
                if (now - last < every) continue;
                s.RemindedAt = now;
                list.Add(new ToastRequest
                {
                    SessionId = s.Id, Hwnd = s.Hwnd, Host = s.Where, Kind = ToastKind.NeedsYou, Background = ParentOf(s) != null,
                    Title = s.Project + " still needs you",
                    Detail = "It has been waiting " + Status.Duration((now - s.BlockedSince).TotalSeconds) + ".",
                });
            }
            return list;
        }

        static string GuessType(string message)
        {
            string m = (message ?? "").ToLowerInvariant();
            if (m.Contains("waiting for your input")) return "idle_prompt";
            if (m.Contains("permission")) return "permission_prompt";
            return "";
        }

        // Called every few seconds. Returns true when anything changed.
        public bool Sweep(DateTime now, Func<Session, bool> alive, Func<Session, TranscriptProbe> probe,
            Func<Session, string, DateTime?> subagentWrite = null)
        {
            bool changed = false;
            foreach (var s in map.Values.ToList())
            {
                if (s.Pid != 0 ? !alive(s) : now - s.LastEvent > ForgetUntracked)
                {
                    map.Remove(s.Id);
                    changed = true;
                    continue;
                }
                // A subagent counts as alive while its own transcript keeps being written.
                foreach (var id in s.Subagents.Keys.ToList())
                {
                    var sub = s.Subagents[id];
                    DateTime? wrote = subagentWrite?.Invoke(s, id);
                    if (wrote.HasValue && wrote.Value > sub.Seen) sub.Seen = wrote.Value;
                    if (now - sub.Seen > StaleWorking) { s.Subagents.Remove(id); changed = true; }
                }
                if (s.State != AgentState.Working) continue;

                var p = probe(s);
                if (p != null && p.Modified > s.TranscriptSeen)
                {
                    s.TranscriptSeen = p.Modified;
                    if (p.Modified > s.LastActivity) s.LastActivity = p.Modified;
                    if (p.EndsInInterrupt)
                    {
                        // Esc in Claude Code skips the Stop hook; the transcript records it instead.
                        s.State = AgentState.Waiting;
                        s.Blocked = false;
                        changed = true;
                        continue;
                    }
                }
                if (now - s.LastActivity > StaleWorking)
                {
                    s.State = AgentState.Waiting;
                    s.Blocked = false;
                    changed = true;
                }
            }
            return changed;
        }
    }

    // One line of the session list: a session, or one of its subagents (Subagent set).
    sealed class TreeRow
    {
        public Session Session;
        public string SubagentId;
        public Subagent Subagent;
        public int Depth;
    }

    sealed class TranscriptProbe
    {
        public DateTime Modified;
        public bool EndsInInterrupt;
    }

    static class Transcript
    {
        const int TailBytes = 256 * 1024;

        public static List<string> TailLines(string path)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return lines;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long start = Math.Max(0, fs.Length - TailBytes);
                fs.Seek(start, SeekOrigin.Begin);
                var buf = new byte[fs.Length - start];
                int read = 0;
                while (read < buf.Length)
                {
                    int n = fs.Read(buf, read, buf.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                string text = Encoding.UTF8.GetString(buf, 0, read);
                var parts = text.Split('\n');
                // The first piece is a partial line unless we read from byte zero.
                for (int i = start > 0 ? 1 : 0; i < parts.Length; i++)
                    if (parts[i].Trim().Length > 0) lines.Add(parts[i]);
            }
            return lines;
        }

        public static string LastAssistantText(IList<string> lines)
        {
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                var o = Entry(lines[i]);
                if (o == null || o.Str("type") != "assistant") continue;
                string text = TextOf(o.Obj("message"));
                if (text.Length > 0) return text;
            }
            return "";
        }

        public static bool EndsInInterrupt(IList<string> lines)
        {
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                var o = Entry(lines[i]);
                string type = o?.Str("type");
                if (type == "assistant") return false;
                if (type != "user") continue;
                return TextOf(o.Obj("message")).TrimStart().StartsWith("[Request interrupted by user", StringComparison.Ordinal);
            }
            return false;
        }

        static JObj Entry(string line) => Json.TryParse(line, out var v) ? v as JObj : null;

        static string TextOf(JObj message)
        {
            if (message == null) return "";
            var content = message["content"];
            if (content is string s) return s;
            if (!(content is List<object> blocks)) return "";
            var sb = new StringBuilder();
            foreach (var b in blocks)
                if (b is JObj block && block.Str("type") == "text")
                    sb.Append(block.Str("text")).Append('\n');
            return sb.ToString().Trim();
        }
    }

    static class Snippet
    {
        static readonly Regex Fence = new Regex("```.*?(```|$)", RegexOptions.Singleline);
        static readonly Regex Marks = new Regex(@"(\*\*|__|`|^#+\s*|^\s*[-*>]\s+)", RegexOptions.Multiline);
        static readonly Regex Space = new Regex(@"\s+");

        public static string Clean(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            string t = Fence.Replace(text, " ");
            t = Marks.Replace(t, "");
            t = Space.Replace(t, " ").Trim();
            return t.Length > 600 ? t.Substring(0, 600) : t;
        }
    }

    static class ClaudeHooks
    {
        public static string[] Events => Agents.Get("claude").Events;

        public static bool IsOurs(string command) =>
            command != null
            && command.IndexOf("aevalsistant", StringComparison.OrdinalIgnoreCase) >= 0
            && command.IndexOf("--hook", StringComparison.Ordinal) >= 0;

        public static string Apply(string settingsText, string command, out bool changed) =>
            Agents.Apply(Agents.Get("claude"), settingsText, command, out changed);

        public static bool IsInstalled(string settingsText, string command) =>
            Agents.IsInstalled(Agents.Get("claude"), settingsText, command);
    }

    sealed class Settings
    {
        public bool Hooks = true;
        public readonly HashSet<string> AgentsOff = new HashSet<string>();   // agent ids the user turned off one by one
        public bool Startup = true;
        public bool KeepAwake = true;
        public bool LidAwake;
        public bool DisplayOn;
        public string LidSaved = "";   // "scheme-guid|ac|dc" while our lid override is in effect
        public bool Welcomed;
        public bool Chats = true;      // watch Claude and ChatGPT desktop chats
        public bool AutoUpdate = true;
        public string LastVersion = "";   // version that last ran, to say "updated to" once
        public bool NotifyDone = true;
        public bool NotifyBackgroundDone;
        public bool NotifyNeedsYou = true;
        public bool NotifyBackgroundNeedsYou = true;
        public bool Sound = true;
        public bool ExpandOnHover = true;
        public bool AltTab = true;
        public int CardTime = 1;          // 0 short, 1 normal, 2 long
        public bool ShowChildren = true;  // list subagents and background sessions under their session
        // Apps that keep the PC awake while they are open, as if an agent were working.
        public readonly List<WatchedApp> WatchApps = new List<WatchedApp> { new WatchedApp { Name = "blender", Label = "Blender", On = true } };
        public bool WatchAppsOn = true;
        public bool NotifyAway;           // no card when the agent's window is already in front
        public int MinTurn;               // index into MinTurnSeconds: finish cards only after longer turns
        public bool Remind;               // repeat the card while an agent still needs you
        public int RemindAfter = 1;       // index into RemindMinutes

        public static readonly double[] MinTurnSeconds = { 0, 60, 300, 900 };
        public static readonly double[] RemindMinutes = { 2, 5, 10 };

        // The "Notify me" choice: always, only when agents need you, or never.
        public int NotifyMode
        {
            get => !NotifyNeedsYou && !NotifyDone ? 2 : !NotifyDone ? 1 : 0;
            set { NotifyDone = value == 0; NotifyNeedsYou = value != 2; }
        }

        public static Settings Load(string path)
        {
            var s = new Settings();
            if (!File.Exists(path)) return s;
            foreach (var line in File.ReadAllLines(path))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                bool b = v == "1";
                switch (k)
                {
                    case "hooks": s.Hooks = b; break;
                    case "agentsOff": foreach (var id in v.Split(',')) if (id.Trim().Length > 0) s.AgentsOff.Add(id.Trim()); break;
                    case "startup": s.Startup = b; break;
                    case "awake": s.KeepAwake = b; break;
                    case "lid": s.LidAwake = b; break;
                    case "display": s.DisplayOn = b; break;
                    case "lidSaved": s.LidSaved = v; break;
                    case "welcomed": s.Welcomed = b; break;
                    case "chats": s.Chats = b; break;
                    case "autoUpdate": s.AutoUpdate = b; break;
                    case "lastVersion": s.LastVersion = v; break;
                    case "notifyDone": s.NotifyDone = b; break;
                    case "notifyBackgroundDone": s.NotifyBackgroundDone = b; break;
                    case "notifyNeedsYou": s.NotifyNeedsYou = b; break;
                    case "notifyBackgroundNeedsYou": s.NotifyBackgroundNeedsYou = b; break;
                    case "sound": s.Sound = b; break;
                    case "expand": s.ExpandOnHover = b; break;
                    case "altTab": s.AltTab = b; break;
                    case "cardTime": if (int.TryParse(v, out int t) && t >= 0 && t <= 2) s.CardTime = t; break;
                    case "children": s.ShowChildren = b; break;
                    case "watchApps": s.WatchApps.Clear(); s.WatchApps.AddRange(WatchedApp.ParseList(v)); break;
                    case "watchAppsOn": s.WatchAppsOn = b; break;
                    case "notifyAway": s.NotifyAway = b; break;
                    case "minTurn": if (int.TryParse(v, out int m) && m >= 0 && m < MinTurnSeconds.Length) s.MinTurn = m; break;
                    case "remind": s.Remind = b; break;
                    case "remindAfter": if (int.TryParse(v, out int ra) && ra >= 0 && ra < RemindMinutes.Length) s.RemindAfter = ra; break;
                }
            }
            return s;
        }

        public void Save(string path)
        {
            string B(bool b) => b ? "1" : "0";
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, string.Join("\n", new[]
            {
                "hooks=" + B(Hooks), "agentsOff=" + string.Join(",", AgentsOff.OrderBy(x => x)), "startup=" + B(Startup),
                "awake=" + B(KeepAwake), "lid=" + B(LidAwake), "display=" + B(DisplayOn), "lidSaved=" + LidSaved,
                "welcomed=" + B(Welcomed), "chats=" + B(Chats), "autoUpdate=" + B(AutoUpdate), "lastVersion=" + LastVersion,
                "notifyDone=" + B(NotifyDone), "notifyBackgroundDone=" + B(NotifyBackgroundDone),
                "notifyNeedsYou=" + B(NotifyNeedsYou), "notifyBackgroundNeedsYou=" + B(NotifyBackgroundNeedsYou),
                "sound=" + B(Sound), "expand=" + B(ExpandOnHover), "altTab=" + B(AltTab),
                "cardTime=" + CardTime.ToString(CultureInfo.InvariantCulture), "children=" + B(ShowChildren),
                "watchApps=" + WatchedApp.FormatList(WatchApps), "watchAppsOn=" + B(WatchAppsOn), "notifyAway=" + B(NotifyAway),
                "minTurn=" + MinTurn.ToString(CultureInfo.InvariantCulture), "remind=" + B(Remind),
                "remindAfter=" + RemindAfter.ToString(CultureInfo.InvariantCulture), "",
            }));
        }

        // Whether a card should show for this request. Background cards also need their
        // parent setting on, since the menu nests them under it.
        public bool Wants(ToastRequest r) =>
            r.Kind == ToastKind.Info
            || (r.Kind == ToastKind.NeedsYou ? NotifyNeedsYou && (!r.Background || NotifyBackgroundNeedsYou)
                                             : NotifyDone && (!r.Background || NotifyBackgroundDone)
                                               && !(MinTurn > 0 && r.TurnSeconds > 0 && r.TurnSeconds < MinTurnSeconds[MinTurn]));

        // Seconds a card stays up while you are at the keyboard.
        public double Seconds(ToastKind kind)
        {
            double normal = kind == ToastKind.NeedsYou ? 10 : kind == ToastKind.Info ? 5 : 8;
            return normal * (CardTime == 0 ? 0.6 : CardTime == 2 ? 1.75 : 1);
        }
    }

    // The short state shown for a session in the toast list and the tray menu.
    static class Status
    {
        public static RowState State(Session s) =>
            s.Blocked ? RowState.NeedsYou : s.Busy ? RowState.Working : RowState.Done;

        public static string Text(Session s, DateTime now)
        {
            int n = s.Subagents.Count;
            string subs = n == 1 ? "1 subagent" : n + " subagents";
            if (s.Blocked) return "needs you";
            if (s.State == AgentState.Working) return Join("working", Age(now - s.WorkingSince)) + (n > 0 ? ", " + subs : "");
            if (n > 0) return subs + " running";
            return Join("done", Age(now - s.LastEvent), "ago");
        }

        // "1h 4m worked", or nothing under a minute.
        public static string Worked(Session s) => s.Worked < 60 ? "" : Duration(s.Worked) + " worked";

        public static string Duration(double seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return t.TotalMinutes < 1 ? "under a minute" : t.TotalHours < 1 ? (int)t.TotalMinutes + "m" : (int)t.TotalHours + "h " + t.Minutes + "m";
        }

        // A subagent's line: "running 4m".
        public static string Text(Subagent a, DateTime now) => Join("running", Age(now - a.Started));

        static string Age(TimeSpan t) =>
            t.TotalMinutes < 1 || t.TotalDays > 7 ? "" : t.TotalHours < 1 ? (int)t.TotalMinutes + "m" : (int)t.TotalHours + "h " + t.Minutes + "m";

        static string Join(string word, string age, string suffix = "") =>
            age.Length == 0 ? word : word + " " + age + (suffix.Length > 0 ? " " + suffix : "");
    }

    // An app that keeps the PC awake while it is open. Name is the process name in lower case
    // ("blender"); Label is what Settings shows ("Blender").
    sealed class WatchedApp
    {
        public string Name = "";
        public string Label = "";
        public bool On = true;

        // "blender|Blender|1,obs64|OBS Studio|0"
        public static List<WatchedApp> ParseList(string v)
        {
            var list = new List<WatchedApp>();
            foreach (var entry in (v ?? "").Split(','))
            {
                var f = entry.Split('|');
                string name = f[0].Trim().ToLowerInvariant();
                if (name.Length == 0 || list.Any(a => a.Name == name)) continue;
                list.Add(new WatchedApp
                {
                    Name = name,
                    Label = f.Length > 1 && f[1].Trim().Length > 0 ? f[1].Trim() : name,
                    On = f.Length < 3 || f[2].Trim() != "0",
                });
            }
            return list;
        }

        public static string FormatList(IEnumerable<WatchedApp> apps)
        {
            string Clean(string x) => (x ?? "").Replace(",", " ").Replace("|", " ").Trim();
            return string.Join(",", apps.Where(a => Clean(a.Name).Length > 0)
                .Select(a => Clean(a.Name) + "|" + Clean(a.Label) + "|" + (a.On ? "1" : "0")));
        }
    }

    static class WatchMatch
    {
        // "blender" matches blender, Blender.exe, blender-launcher and Blender.app, so every part
        // of an app counts as it being open.
        public static bool Matches(string watch, string process)
        {
            string w = (watch ?? "").Trim().ToLowerInvariant(), p = (process ?? "").Trim().ToLowerInvariant();
            if (w.Length == 0 || p.Length == 0) return false;
            if (p.EndsWith(".exe", StringComparison.Ordinal) || p.EndsWith(".app", StringComparison.Ordinal)) p = p.Substring(0, p.Length - 4);
            if (p == w) return true;
            return p.Length > w.Length && p.StartsWith(w, StringComparison.Ordinal) && "-_. ".IndexOf(p[w.Length]) >= 0;
        }
    }

    // Time keys: "agent:claude", "app:blender".
    static class TimeKeys
    {
        public static string Agent(string id) => "agent:" + id;
        public static string App(string name) => "app:" + name;

        public static string Label(string key, IEnumerable<WatchedApp> apps)
        {
            if (key.StartsWith("agent:", StringComparison.Ordinal))
            {
                string id = key.Substring(6);
                return id == "chat" ? "Chat apps" : Agents.Get(id).Name;
            }
            if (key.StartsWith("app:", StringComparison.Ordinal))
            {
                string name = key.Substring(4);
                return apps.FirstOrDefault(a => a.Name == name)?.Label ?? name;
            }
            return key;
        }
    }

    // Seconds each kind of agent spent working and each watched app stayed open: today and in total.
    sealed class TimeLog
    {
        public const double MaxStep = 60;   // a longer gap is sleep or a stalled app, not work

        public string Day = "";
        public readonly Dictionary<string, double> Today = new Dictionary<string, double>();
        public readonly Dictionary<string, double> Total = new Dictionary<string, double>();
        DateTime? lastTick;

        // Returns the seconds credited to each active key.
        public double Tick(DateTime now, string day, IEnumerable<string> active)
        {
            double dt = lastTick == null ? 0 : Math.Min(MaxStep, Math.Max(0, (now - lastTick.Value).TotalSeconds));
            lastTick = now;
            if (day != Day) { Day = day; Today.Clear(); }
            foreach (var k in active.Distinct())
            {
                Today[k] = (Today.TryGetValue(k, out var a) ? a : 0) + dt;
                Total[k] = (Total.TryGetValue(k, out var b) ? b : 0) + dt;
            }
            return dt;
        }

        public void Reset() { Today.Clear(); Total.Clear(); }

        public static string DayString(DateTime local) => local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // "Claude Code 2h 5m, Blender 1h 10m", longest first; empty when nothing reached a minute.
        public static string Summary(Dictionary<string, double> table, Func<string, string> label) =>
            string.Join(", ", table.Where(kv => kv.Value >= 60)
                .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => label(kv.Key) + " " + Status.Duration(kv.Value)));

        public static TimeLog Load(string path)
        {
            var log = new TimeLog();
            if (!File.Exists(path)) return log;
            foreach (var line in File.ReadAllLines(path))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                if (k == "day") { log.Day = v; continue; }
                if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double secs)) continue;
                if (k.StartsWith("today.", StringComparison.Ordinal)) log.Today[k.Substring(6)] = secs;
                else if (k.StartsWith("total.", StringComparison.Ordinal)) log.Total[k.Substring(6)] = secs;
            }
            return log;
        }

        public void Save(string path)
        {
            string N(double v) => Math.Round(v).ToString(CultureInfo.InvariantCulture);
            var lines = new List<string> { "day=" + Day };
            foreach (var kv in Today.OrderBy(kv => kv.Key, StringComparer.Ordinal)) lines.Add("today." + kv.Key + "=" + N(kv.Value));
            foreach (var kv in Total.OrderBy(kv => kv.Key, StringComparer.Ordinal)) lines.Add("total." + kv.Key + "=" + N(kv.Value));
            lines.Add("");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, string.Join("\n", lines));
        }
    }

    static class Plural
    {
        public static string Agents(int n) => n.ToString(CultureInfo.InvariantCulture) + (n == 1 ? " agent" : " agents");
    }
}

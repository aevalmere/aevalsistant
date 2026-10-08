using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;

namespace Aevalsistant
{
    static partial class Tests
    {
        static int failed, passed;

        static void Check(bool ok, string name)
        {
            if (ok) passed++;
            else { failed++; Console.WriteLine("FAIL " + name); }
        }

        static int Main(string[] args)
        {
            JsonRoundTrip();
            HooksInstallMergeRemove();
            Sessions();
            TranscriptParsing();
            Envelope();
            OtherAgents();
            HookPaths();
            ChatWatching();
            Check(Updater.ParseTag("v1.2.0") == new Version(1, 2, 0, 0) && Updater.ParseTag("1.10") == new Version(1, 10, 0, 0)
                && Updater.ParseTag("v1.2.0") > new Version(1, 1, 0, 0) && Updater.ParseTag("v1.1.0") == new Version(1, 1, 0, 0)
                && Updater.ParseTag("nightly") == null, "update: release tags compare against the build version");
            UpdateAssets();
            Easing();
            if (args.Length > 0 && args[0] == "--menu") { MenuPreview.Run(); return 0; }
            if (args.Length > 1 && args[0] == "--update-from")
            {
                // Runs the real update check against a local stand-in for GitHub.
                Updater.ApiBase = args[1];
                var r = Updater.Check();
                Console.WriteLine("latest=" + r.Latest + " downloaded=" + r.Downloaded + " problem=" + r.Problem);
                return r.Downloaded != null ? 0 : 1;
            }
            if (args.Length > 0) Previews(args[0]);
            Console.WriteLine($"{passed} passed, {failed} failed");
            return failed == 0 ? 0 : 1;
        }

        // A user folder with a space used to be shortened along with everything after it
        // (AEVALS~1\AEVALS~1.EXE), so the app stopped recognizing its own hooks and added another
        // set on every sync.
        static void HookPaths()
        {
            const string Exe = @"C:\Users\John Smith\AppData\Local\Aevalsistant\Aevalsistant.exe";
            string shortened = Agents.HookPath(Exe, p => p == @"C:\Users\John Smith" ? @"C:\Users\JOHNSM~1" : null);
            Check(shortened == "C:/Users/JOHNSM~1/AppData/Local/Aevalsistant/Aevalsistant.exe", "hook path: only the folder with a space is shortened");
            Check(ClaudeHooks.IsOurs(shortened + " --hook"), "hook path: shortened command is still recognized");
            var claude = Agents.Get("claude");
            string once = Agents.Apply(claude, null, shortened + " --hook", out _);
            string twice = Agents.Apply(claude, once, shortened + " --hook", out bool again);
            Check(!again && twice == once, "hook path: syncing again adds nothing");

            string quoted = Agents.HookPath(Exe, p => p);   // 8.3 names turned off
            Check(quoted == "\"C:/Users/John Smith/AppData/Local/Aevalsistant/Aevalsistant.exe\"", "hook path: quoted when there is no short name");
            Check(Agents.Apply(Agents.Get("windsurf"), null, quoted + " --hook windsurf", out _).Contains("\"powershell\": \"& \\\"C:/Users/John Smith"), "hook path: windsurf's powershell command calls a quoted path with &");
            Check(Agents.HookPath(@"C:\Users\O'Brien\AppData\Local\Aevalsistant\Aevalsistant.exe", p => p).StartsWith("\""), "hook path: apostrophe is quoted");
            Check(Agents.HookPath(@"C:\Users\andy\AppData\Local\Aevalsistant\Aevalsistant.exe", p => throw new InvalidOperationException("not needed"))
                == "C:/Users/andy/AppData/Local/Aevalsistant/Aevalsistant.exe", "hook path: a plain path is left alone");
        }

        // The shape of api.github.com/repos/{repo}/releases/latest, trimmed to what the updater reads.
        static void UpdateAssets()
        {
            string hash = new string('a', 60) + "B0c9";
            JObj Release(string assets) => (JObj)Json.Parse("{\"tag_name\":\"v1.3.0\",\"assets\":[" + assets + "]}");
            string Asset(string name, string digest) =>
                "{\"name\":\"" + name + "\",\"browser_download_url\":\"https://example.test/" + name + "\"" + (digest == null ? "" : ",\"digest\":" + Json.Quote(digest)) + "}";

            bool found = Updater.FindAsset(Release(Asset("Source.zip", null) + "," + Asset("Aevalsistant.exe", "sha256:" + hash)), out var url, out var sha);
            Check(found && url == "https://example.test/Aevalsistant.exe" && sha == hash.ToLowerInvariant(), "update: exe and its sha256 digest found");
            Check(Updater.FindAsset(Release(Asset("Aevalsistant.exe", null)), out _, out sha) && sha == null, "update: missing digest reported as no checksum");
            Check(Updater.FindAsset(Release(Asset("Aevalsistant.exe", "sha512:" + hash)), out _, out sha) && sha == null, "update: other digest algorithms ignored");
            Check(Updater.FindAsset(Release(Asset("Aevalsistant.exe", "sha256:abc")), out _, out sha) && sha == null, "update: truncated digest ignored");
            Check(!Updater.FindAsset(Release(Asset("Aevalsistant.zip", "sha256:" + hash)), out _, out _), "update: release without the exe");
            Check(!Updater.FindAsset((JObj)Json.Parse("{\"tag_name\":\"v1.3.0\"}"), out _, out _), "update: release without assets");
        }

        static void JsonRoundTrip()
        {
            string src = "{\n  \"b\": 1,\n  \"a\": [\n    1.50,\n    -2e3,\n    true,\n    null,\n    \"x\\\"y\\u00e9\\n\"\n  ],\n  \"c\": {}\n}";
            var v = Json.Parse(src);
            string expected = src.Replace("\\u00e9", "\u00e9");   // escapes are decoded; everything else is byte-identical
            Check(Json.Write(v) == expected, "json: order, number text, strings survive a round trip");
            Check(!Json.TryParse("{\"a\":1,}", out _), "json: trailing comma rejected");
            Check(!Json.TryParse("{\"a\":1} x", out _), "json: trailing garbage rejected");
            Check(Json.TryParse("\uFEFF{}", out _), "json: BOM tolerated");
        }

        const string Cmd = "C:/Users/andy/AppData/Local/Aevalsistant/Aevalsistant.exe --hook";

        static void HooksInstallMergeRemove()
        {
            // fresh install, no file
            string a = ClaudeHooks.Apply(null, Cmd, out bool ch1);
            Check(ch1 && ClaudeHooks.IsInstalled(a, Cmd), "hooks: install into missing settings");

            // user settings with their own Stop hook, other keys, and odd numbers
            string user = "{\n  \"model\": \"opus\",\n  \"cleanupPeriodDays\": 30,\n  \"hooks\": {\n    \"Stop\": [\n      {\n        \"hooks\": [\n          {\n            \"type\": \"command\",\n            \"command\": \"~/.claude/my-stop.sh\",\n            \"timeout\": 5\n          }\n        ]\n      }\n    ],\n    \"PreToolUse\": [\n      {\n        \"matcher\": \"Bash\",\n        \"hooks\": [ { \"type\": \"command\", \"command\": \"guard.sh\" } ]\n      }\n    ]\n  },\n  \"ratio\": 1.50\n}";
            string b = ClaudeHooks.Apply(user, Cmd, out bool ch2);
            var root = (JObj)Json.Parse(b);
            var stop = (List<object>)root.Obj("hooks")["Stop"];
            Check(ch2 && ClaudeHooks.IsInstalled(b, Cmd), "hooks: install alongside user hooks");
            Check(stop.Count == 2 && b.Contains("~/.claude/my-stop.sh") && b.Contains("guard.sh"), "hooks: user hooks kept");
            Check(b.Contains("\"ratio\": 1.50") && b.Contains("\"cleanupPeriodDays\": 30"), "hooks: numbers kept verbatim");
            Check(root.Items[0].Key == "model" && root.Items.Last().Key == "ratio", "hooks: key order kept");
            Check(b.Contains("\"async\": true"), "hooks: async set");
            var endEntry = (JObj)((List<object>)((JObj)((List<object>)root.Obj("hooks")["SessionEnd"])[0])["hooks"])[0];
            Check(endEntry["async"] == null, "hooks: SessionEnd stays synchronous");

            // idempotent
            ClaudeHooks.Apply(b, Cmd, out bool ch3);
            Check(!ch3, "hooks: second install is a no-op");

            // moved exe: old entry replaced, not duplicated
            string moved = "D:/Tools/Aevalsistant/Aevalsistant.exe --hook";
            string c = ClaudeHooks.Apply(b, moved, out bool ch4);
            int count = c.Split(new[] { "--hook" }, StringSplitOptions.None).Length - 1;
            Check(ch4 && count == ClaudeHooks.Events.Length && !c.Contains("C:/Users/andy"), "hooks: path change replaces entries");

            // uninstall leaves only the user's hooks
            string d = ClaudeHooks.Apply(c, null, out bool ch5);
            var droot = (JObj)Json.Parse(d);
            Check(ch5 && !d.Contains("--hook") && d.Contains("my-stop.sh") && droot.Obj("hooks")["UserPromptSubmit"] == null, "hooks: uninstall removes only ours");

            // uninstall when ours were the only hooks drops the hooks key
            string e = ClaudeHooks.Apply(ClaudeHooks.Apply("{\"model\":\"opus\"}", Cmd, out _), null, out _);
            Check(((JObj)Json.Parse(e))["hooks"] == null, "hooks: empty hooks object removed");

            // broken input is refused, never overwritten
            bool threw = false;
            try { ClaudeHooks.Apply("{ \"model\": ", Cmd, out _); } catch (FormatException) { threw = true; }
            Check(threw, "hooks: invalid JSON refused");
            threw = false;
            try { ClaudeHooks.Apply("{\"hooks\": []}", Cmd, out _); } catch (FormatException) { threw = true; }
            Check(threw, "hooks: non-object hooks refused");
            Check(!ClaudeHooks.IsOurs("~/.claude/aevalsistant-notes.sh") && ClaudeHooks.IsOurs(Cmd), "hooks: ownership test");
        }

        static HookEvent Ev(string name, string sid = "s1", long at = 0, string msg = "", string type = "", string last = "") =>
            new HookEvent { Name = name, SessionId = sid, Cwd = @"C:\code\aevalrena", Message = msg, NotificationType = type, LastAssistant = last, At = at, Hwnd = 42, Pid = 7, PidStart = 99 };

        static void Sessions()
        {
            var t = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
            var book = new SessionBook();
            Check(book.Apply(Ev("UserPromptSubmit", at: 10), t) == null && book.BusyCount == 1, "sessions: prompt starts work");

            var perm = book.Apply(Ev("Notification", msg: "Claude needs your permission to use Bash", type: "permission_prompt"), t);
            Check(perm != null && perm.Kind == ToastKind.NeedsYou && book.BusyCount == 1 && book.Get("s1").Blocked, "sessions: permission prompt toasts, stays awake");

            var done = book.Apply(Ev("Stop", at: 20, last: "**Done.** Tests pass."), t);
            Check(done != null && done.Host == "" && done.Kind == ToastKind.Done && done.Title == "aevalrena finished" && done.Detail == "Done. Tests pass." && book.BusyCount == 0, "sessions: stop toasts with snippet");
            Check(book.Apply(Ev("Notification", msg: "Claude is waiting for your input", type: "idle_prompt"), t) == null, "sessions: idle prompt after stop is not repeated");

            // async reordering: a prompt that started before the stop arrives late
            Check(book.Apply(Ev("UserPromptSubmit", at: 15), t) == null && book.BusyCount == 0, "sessions: stale prompt ignored");
            book.Apply(Ev("UserPromptSubmit", at: 30), t);
            Check(book.BusyCount == 1, "sessions: newer prompt counts");
            Check(book.Apply(Ev("Stop", at: 25), t) == null && book.BusyCount == 1, "sessions: stale stop ignored");

            var idle = book.Apply(Ev("Notification", msg: "Claude is waiting for your input"), t);
            Check(idle != null && idle.Title == "aevalrena is waiting" && book.BusyCount == 0, "sessions: idle prompt without stop toasts once (type guessed)");

            var fail = book.Apply(Ev("StopFailure", at: 40), t);
            Check(fail != null && fail.Kind == ToastKind.NeedsYou, "sessions: stop failure toasts");

            // sweep: interrupt in transcript ends work without a toast
            book.Apply(Ev("UserPromptSubmit", "s2", at: 50), t);
            bool changed = book.Sweep(t.AddSeconds(5), s => true, s => new TranscriptProbe { Modified = t.AddSeconds(3), EndsInInterrupt = s.Id == "s2" });
            Check(changed && book.Get("s2").State == AgentState.Waiting, "sweep: interrupt detected");

            // sweep: stale work times out
            book.Apply(Ev("UserPromptSubmit", "s3", at: 60), t);
            book.Sweep(t.AddMinutes(44), s => true, s => null);
            Check(book.Get("s3").State == AgentState.Working, "sweep: 44 minutes is not stale");
            book.Sweep(t.AddMinutes(46), s => true, s => null);
            Check(book.Get("s3").State == AgentState.Waiting, "sweep: 46 minutes is stale");

            // sweep: dead process is forgotten
            book.Sweep(t.AddMinutes(47), s => s.Id != "s3", s => null);
            Check(book.Get("s3") == null && book.Get("s1") != null, "sweep: dead claude process removed");

            // subagents: a background one outlives the parent's turn and keeps the PC awake
            var sb = new SessionBook();
            HookEvent Sub(string name, string id) => new HookEvent { Name = name, SessionId = "p", AgentId = id, Cwd = @"C:\code\forge", Transcript = @"C:\Users\a\.claude\projects\x\p.jsonl" };
            sb.Apply(new HookEvent { Name = "UserPromptSubmit", SessionId = "p", Cwd = @"C:\code\forge", Transcript = @"C:\Users\a\.claude\projects\x\p.jsonl" }, t);
            sb.Apply(Sub("SubagentStart", "a1"), t);
            sb.Apply(Sub("SubagentStart", "a2"), t);
            Check(Status.Text(sb.Get("p"), t.AddMinutes(3)) == "working 3m, 2 subagents", "subagents: counted while the parent works");
            sb.Apply(Sub("SubagentStop", "a1"), t);
            var waitReq = sb.Apply(new HookEvent { Name = "Stop", SessionId = "p" }, t);
            Check(waitReq.Title == "forge is waiting" && sb.BusyCount == 1 && Status.Text(sb.Get("p"), t) == "1 subagent running"
                && Status.State(sb.Get("p")) == RowState.Working, "subagents: background one keeps the session busy after Stop");
            Check(sb.Get("p").SubagentTranscript("a2").Replace('\\', '/').EndsWith("projects/x/p/subagents/agent-a2.jsonl"), "subagents: transcript path");
            sb.Sweep(t.AddMinutes(60), x => true, x => null, (x, id) => t.AddMinutes(50));
            Check(sb.BusyCount == 1, "subagents: recent transcript write keeps it alive");
            sb.Sweep(t.AddMinutes(100), x => true, x => null, (x, id) => t.AddMinutes(50));
            Check(sb.BusyCount == 0, "subagents: silent for 45 minutes is dropped");
            sb.Apply(Sub("SubagentStop", "a3"), t);
            sb.Apply(Sub("SubagentStart", "a3"), t);
            Check(sb.BusyCount == 0, "subagents: stop that arrived before its start cancels it");
            Check(Status.Text(sb.Get("p"), t.AddMinutes(4)) == "done 4m ago", "status: done age");

            book.Apply(Ev("SessionEnd"), t);
            Check(book.Get("s1") == null, "sessions: session end removes");

            Check(new Session { Cwd = @"C:\code\kymarion\" }.Project == "kymarion" && new Session { Cwd = "" }.Project == "Claude Code"
                && new Session { Cwd = "C:\\" }.Project == "C:", "sessions: project names");
        }

        static void TranscriptParsing()
        {
            var lines = new List<string>
            {
                "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"fix the build\"}}",
                "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"Looking at the build.\"}]}}",
                "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Bash\"}]}}",
                "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"content\":\"ok\"}]}}",
                "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"thinking\",\"thinking\":\"\"}]}}",
                "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"## Fixed\\nThe `tsconfig` path was wrong.\"}]}}",
                "{\"type\":\"ai-title\",\"title\":\"x\"}",
                "not json at all",
            };
            Check(Snippet.Clean(Transcript.LastAssistantText(lines)) == "Fixed The tsconfig path was wrong.", "transcript: last text, markdown stripped");
            Check(!Transcript.EndsInInterrupt(lines), "transcript: no interrupt");
            lines.Add("{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"[Request interrupted by user for tool use]\"}]}}");
            lines.Add("{\"type\":\"last-prompt\"}");
            Check(Transcript.EndsInInterrupt(lines), "transcript: interrupt detected past metadata lines");

            // tail read: a file larger than the tail window starts on a whole line
            string path = Path.Combine(Path.GetTempPath(), "aeval-tail-test.jsonl");
            var sb = new StringBuilder();
            for (int i = 0; i < 6000; i++) sb.Append("{\"type\":\"user\",\"message\":{\"content\":\"padding line number " + i + "\"}}\n");
            sb.Append("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"tail works\"}]}}\n");
            File.WriteAllText(path, sb.ToString());
            var tail = Transcript.TailLines(path);
            Check(new FileInfo(path).Length > 256 * 1024 && tail.All(l => l.StartsWith("{")) && Transcript.LastAssistantText(tail) == "tail works", "transcript: tail of large file");
            File.Delete(path);

            // the real transcript of this build session, if present
            string real = Environment.GetEnvironmentVariable("AEVAL_REAL_TRANSCRIPT");
            if (!string.IsNullOrEmpty(real) && File.Exists(real))
            {
                var rl = Transcript.TailLines(real);
                int typed = rl.Count(l => Json.TryParse(l, out var o) && (o as JObj)?.Str("type") != null);
                Check(rl.Count > 0 && typed == rl.Count, "transcript: every line of a real Claude Code transcript parses");
                Console.WriteLine("real transcript: " + rl.Count + " lines, interrupted=" + Transcript.EndsInInterrupt(rl) + ", last text length=" + Transcript.LastAssistantText(rl).Length);
            }
        }

        static void Envelope()
        {
            string raw = "{\"session_id\":\"abc\",\"hook_event_name\":\"Stop\",\"cwd\":\"C:\\\\code\\\\forge\",\"transcript_path\":\"C:\\\\t.jsonl\",\"last_assistant_message\":\"All green.\"}";
            string env = "{\"t\":638640000000000000,\"host\":\"Android Studio\",\"hwnd\":132456,\"pid\":4242,\"pidStart\":1337,\"raw\":" + Json.Quote(raw) + "}";
            var e = HookEvent.FromEnvelope(env);
            Check(e != null && e.Name == "Stop" && e.Hwnd == 132456 && e.Pid == 4242 && e.At == 638640000000000000 && e.Cwd == @"C:\code\forge" && e.LastAssistant == "All green." && e.Host == "Android Studio", "envelope: parsed");
            Check(HookEvent.FromEnvelope("{\"raw\":\"{}\"}") == null && HookEvent.FromEnvelope("garbage") == null, "envelope: junk ignored");
        }

        const string Base = "C:/Users/andy/AppData/Local/Aevalsistant/Aevalsistant.exe --hook";

        static HookEvent Env(string agent, string raw, string ev = "", string pwd = @"C:\code\fallback") =>
            HookEvent.FromEnvelope("{\"t\":1,\"agent\":" + Json.Quote(agent) + ",\"event\":" + Json.Quote(ev) + ",\"pwd\":" + Json.Quote(pwd)
                + ",\"host\":\"Terminal\",\"hwnd\":7,\"pid\":0,\"pidStart\":0,\"raw\":" + Json.Quote(raw) + "}");

        static void OtherAgents()
        {
            // installers: every agent installs, is idempotent, keeps the user's own hooks, and removes cleanly
            foreach (var a in Agents.All)
            {
                string cmd = Agents.CommandFor(a, Base);
                string fresh = Agents.Apply(a, null, cmd, out bool c1);
                Check(c1 && Agents.IsInstalled(a, fresh, cmd), "install: " + a.Id + " into a missing file");
                Agents.Apply(a, fresh, cmd, out bool c2);
                Check(!c2, "install: " + a.Id + " twice is a no-op");
                string removed = Agents.Apply(a, fresh, null, out bool c3);
                Check(c3 && (a.Layout == HookLayout.Own ? removed == null : !removed.Contains("--hook")), "install: " + a.Id + " removes cleanly");
            }

            var codex = Agents.Get("codex");
            string userCodex = "{\n  \"hooks\": {\n    \"PreToolUse\": [\n      {\n        \"matcher\": \"Bash\",\n        \"hooks\": [\n          {\n            \"type\": \"command\",\n            \"command\": \"python3 ~/.codex/hooks/check.py\"\n          }\n        ]\n      }\n    ]\n  }\n}";
            string withOurs = Agents.Apply(codex, userCodex, Agents.CommandFor(codex, Base), out _);
            Check(withOurs.Contains("check.py") && withOurs.Contains("--hook codex") && withOurs.Contains("\"Interrupt\""), "install: codex keeps the user's PreToolUse hook");
            Check(!Agents.Apply(codex, withOurs, null, out _).Contains("--hook") && Agents.Apply(codex, withOurs, null, out _).Contains("check.py"), "install: codex removal keeps theirs");

            var cursor = Agents.Get("cursor");
            string cur = Agents.Apply(cursor, "{\"version\": 1, \"hooks\": {\"stop\": [{\"command\": \"./mine.sh\"}]}}", Agents.CommandFor(cursor, Base), out _);
            var curRoot = (JObj)Json.Parse(cur);
            Check(((List<object>)curRoot.Obj("hooks")["stop"]).Count == 2 && cur.Contains("mine.sh") && cur.Contains("--hook cursor"), "install: cursor flat list keeps theirs");
            Check(((JObj)Json.Parse(Agents.Apply(cursor, null, "x --hook cursor", out _)))["version"] is JNum, "install: cursor file gets a version");
            Check(Agents.Apply(Agents.Get("windsurf"), null, "w --hook windsurf", out _).Contains("\"powershell\": \"w --hook windsurf\""), "install: windsurf has a powershell command");
            string cop = Agents.Apply(Agents.Get("copilot"), null, Agents.CommandFor(Agents.Get("copilot"), Base), out _);
            Check(cop.Contains("--hook copilot agentStop") && cop.Contains("\"version\": 1"), "install: copilot names the event on the command line");
            string oc = Agents.Apply(Agents.Get("opencode"), null, Agents.CommandFor(Agents.Get("opencode"), Base), out _);
            Check(oc.Contains("const EXE = \"C:/Users/andy/AppData/Local/Aevalsistant/Aevalsistant.exe\"") && oc.Contains("session.idle"), "install: opencode plugin points at the exe");
            Check(Agents.CommandFor(Agents.Get("claude"), Base) == Base, "install: claude keeps the bare command");

            // adapters
            var e = Env("codex", "{\"session_id\":\"s9\",\"cwd\":\"C:\\\\code\\\\rover\",\"hook_event_name\":\"Stop\",\"turn_id\":\"t\",\"last_assistant_message\":\"Patched it.\"}");
            Check(e != null && e.Name == "Stop" && e.SessionId == "codex:s9" && e.Cwd == @"C:\code\rover" && e.LastAssistant == "Patched it.", "adapter: codex stop");
            e = Env("codex", "{\"session_id\":\"s9\",\"hook_event_name\":\"PermissionRequest\",\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"rm x\"}}");
            Check(e.Name == "Notification" && e.NotificationType == "permission_prompt" && e.Message == "Codex wants to use Bash" && e.Cwd == @"C:\code\fallback", "adapter: codex permission, cwd falls back to the hook's folder");
            e = Env("gemini", "{\"session_id\":\"g\",\"cwd\":\"/w/site\",\"hook_event_name\":\"AfterAgent\",\"prompt\":\"p\",\"prompt_response\":\"Built the page.\"}");
            Check(e.Name == "Stop" && e.SessionId == "gemini:g" && e.LastAssistant == "Built the page.", "adapter: gemini AfterAgent");
            Check(Env("gemini", "{\"session_id\":\"g\",\"hook_event_name\":\"BeforeAgent\",\"prompt\":\"p\"}").Name == "UserPromptSubmit", "adapter: gemini BeforeAgent");
            e = Env("copilot", "{\"sessionId\":\"c1\",\"timestamp\":1,\"cwd\":\"C:\\\\x\\\\api\",\"transcriptPath\":\"t\",\"stopReason\":\"end_turn\"}", "agentStop");
            Check(e.Name == "Stop" && e.SessionId == "copilot:c1", "adapter: copilot agentStop");
            Check(Env("copilot", "{\"sessionId\":\"c1\",\"error\":{\"message\":\"m\"},\"recoverable\":true}", "errorOccurred") == null, "adapter: copilot recoverable error ignored");
            e = Env("cursor", "{\"conversation_id\":\"k\",\"generation_id\":\"g\",\"hook_event_name\":\"stop\",\"status\":\"aborted\",\"workspace_roots\":[\"/c/app\"]}");
            Check(e.Name == "Interrupt" && e.Cwd == "/c/app" && e.SessionId == "cursor:k", "adapter: cursor aborted stop is an interrupt");
            e = Env("windsurf", "{\"agent_action_name\":\"post_cascade_response\",\"trajectory_id\":\"tr\",\"tool_info\":{\"response\":\"## Done\\nAll set.\"}}");
            Check(e.Name == "Stop" && e.SessionId == "windsurf:tr" && e.LastAssistant.Contains("All set"), "adapter: windsurf response");
            e = Env("opencode", "{\"event\":\"permission\",\"session\":\"o1\",\"cwd\":\"/p/q\",\"message\":\"Run npm test?\"}");
            Check(e.Name == "Notification" && e.Message == "Run npm test?" && e.SessionId == "opencode:o1", "adapter: opencode permission");
            Check(Env("gemini", "{\"session_id\":\"g\",\"hook_event_name\":\"Notification\",\"notification_type\":\"ToolPermission\",\"message\":\"Allow shell?\"}").NotificationType == "permission_prompt"
                && Env("gemini", "{\"session_id\":\"g\",\"hook_event_name\":\"Notification\",\"notification_type\":\"Error\",\"message\":\"Quota\"}") == null, "adapter: gemini only treats tool permission notices as needing you");
            string copilotFile = Agents.Apply(Agents.Get("copilot"), null, "\"C:/Users/A B/Aevalsistant.exe\" --hook copilot", out _);
            Check(copilotFile.Contains("\"powershell\": \"& \\\"C:/Users/A B/Aevalsistant.exe\\\" --hook copilot agentStop\""), "install: copilot's powershell command calls a quoted path with &");

            // session flow for an agent that sends its reply before Stop
            var t = new DateTime(2026, 10, 8, 2, 0, 0, DateTimeKind.Utc);
            var book = new SessionBook();
            book.Apply(Env("cursor", "{\"conversation_id\":\"k\",\"hook_event_name\":\"beforeSubmitPrompt\",\"prompt\":\"x\",\"workspace_roots\":[\"/c/app\"]}"), t);
            book.Apply(Env("cursor", "{\"conversation_id\":\"k\",\"hook_event_name\":\"afterAgentResponse\",\"text\":\"Renamed the module.\"}"), t);
            var r = book.Apply(Env("cursor", "{\"conversation_id\":\"k\",\"hook_event_name\":\"stop\",\"status\":\"completed\"}"), t);
            Check(r != null && r.Title == "app finished" && r.Detail == "Renamed the module." && r.Host == "Cursor \u00B7 Terminal", "flow: cursor reply text and agent label");
            book.Apply(Env("codex", "{\"session_id\":\"z\",\"cwd\":\"/r/x\",\"hook_event_name\":\"UserPromptSubmit\"}"), t);
            Check(book.Apply(Env("codex", "{\"session_id\":\"z\",\"hook_event_name\":\"Interrupt\"}"), t) == null && book.Get("codex:z").State == AgentState.Waiting, "flow: codex interrupt ends work without a toast");
            var gstop = book.Apply(Env("copilot", "{\"sessionId\":\"q\",\"cwd\":\"/a/b\",\"stopReason\":\"end_turn\"}", "agentStop"), t);
            Check(gstop.Detail == "Ready for your next prompt." && gstop.Host == "Copilot CLI \u00B7 Terminal" && !gstop.WantsSnippet, "flow: agent without reply text");
        }

        static void ChatWatching()
        {
            var tr = new ChatTracker();
            Check(tr.Observe(5, 100, "Claude", true, false, false) == null, "chat: idle window is ignored");
            var start = tr.Observe(5, 100, "Claude", true, true, false);
            Check(start != null && start.Name == "UserPromptSubmit" && start.Label == "Chat" && start.Host == "Claude" && start.Agent == "chat", "chat: Stop button starts a session");
            Check(tr.Observe(5, 100, "Claude", true, null, false) == null && tr.Observe(5, 100, "Claude", true, false, false) == null, "chat: one miss is not the end");
            var done = tr.Observe(5, 100, "Claude", true, false, false);
            Check(done != null && done.Name == "Stop", "chat: two misses end it with a notification");
            tr.Observe(6, 200, "ChatGPT", true, true, false);
            tr.Observe(6, 200, "ChatGPT", true, false, true);
            Check(tr.Observe(6, 200, "ChatGPT", true, false, true).Name == "Interrupt", "chat: finished while in front, no notification");
            tr.Observe(7, 300, "ChatGPT", true, true, false);
            Check(tr.Observe(7, 300, "ChatGPT", false, null, false).Name == "SessionEnd", "chat: closed window ends the session");

            var book = new SessionBook();
            var t = DateTime.UtcNow;
            book.Apply(new ChatTracker().Observe(9, 1, "Claude", true, true, false), t);
            var tr2 = new ChatTracker(); tr2.Observe(9, 1, "Claude", true, true, false); tr2.Observe(9, 1, "Claude", true, false, false);
            var req = book.Apply(tr2.Observe(9, 1, "Claude", true, false, false), t);
            Check(req.Title == "Chat finished" && req.Host == "Claude" && req.Detail == "Ready for your next prompt.", "chat: toast reads Chat finished · Claude");
        }

        static void Easing()
        {
            double prev = -1; bool mono = true;
            for (int i = 0; i <= 100; i++) { double y = Ease.Enter(i / 100.0); if (y < prev - 1e-9) mono = false; prev = y; }
            Check(mono && Ease.Enter(0) == 0 && Ease.Enter(1) == 1 && Ease.Enter(0.5) > 0.85, "ease: enter curve decelerates and is monotonic");
            Check(Ease.Exit(0.25) < Ease.Exit(0.5) && Ease.Exit(0.5) < Ease.Exit(0.75) && Ease.Exit(0.1) < 0.1, "ease: exit curve starts gently and rises");
        }

        static void Previews(string dir)
        {
            Directory.CreateDirectory(dir);
            var avatar = Theme.Resource("avatar.png");
            Check(avatar != null, "art: avatar resource embedded");
            var rows = new List<ToastRow>
            {
                new ToastRow { Name = "BiobuzzRobot", Host = "Android Studio", Status = "needs you", State = RowState.NeedsYou },
                new ToastRow { Name = "kymarion-nav", Host = "Terminal", Status = "done 3m ago", State = RowState.Done },
                new ToastRow { Name = "gnce-site", Host = "VS Code", Status = "working 12m", State = RowState.Working },
                new ToastRow { Name = "lecture-study-tool", Host = "Claude", Status = "working", State = RowState.Working },
            };
            var cases = new[]
            {
                new ToastContent { Title = "aevalrena finished", Host = "VS Code", Detail = "Fixed the LAN lobby: clients now reconnect after the host restarts.", Kind = ToastKind.Done, Keycap = true, Rows = rows },
                new ToastContent { Title = "BiobuzzRobot needs you", Host = "Android Studio", Detail = "Claude wants to edit DriveSubsystem.java", Kind = ToastKind.NeedsYou, Keycap = true, Rows = rows.Skip(1).Take(1).ToList() },
                new ToastContent { Title = "Aevalsistant is running", Detail = "Restart open Claude Code sessions once so they report here.", Kind = ToastKind.Info },
            };
            var single = cases[1];
            Check(ToastArt.HitTest(cases[0], 1, 100, 50) == ToastArt.HitHead
                && ToastArt.HitTest(cases[0], 1, 100, ToastArt.Pad + ToastArt.HeadH + ToastArt.ListTop + ToastArt.RowH * 2 + 5) == 2
                && ToastArt.HitTest(cases[0], 1, 2, 50) == ToastArt.HitNone
                && ToastArt.Height(cases[2]) == ToastArt.HeadH, "art: hit test and height follow the list");
            int i = 0;
            foreach (var c in cases)
                foreach (var s in new[] { 1f, 1.5f })
                {
                    using (var card = ToastArt.Render(c, s, i == 1 ? 2 : ToastArt.HitNone, avatar))
                        foreach (var bg in new[] { ("light", Color.FromArgb(0xF3, 0xF4, 0xF6)), ("dark", Color.FromArgb(0x1E, 0x1F, 0x22)) })
                            using (var frame = new Bitmap(card.Width + 40, card.Height + 20))
                            using (var g = Graphics.FromImage(frame))
                            {
                                g.Clear(bg.Item2);
                                g.DrawImage(card, 20, 0);
                                frame.Save(Path.Combine(dir, $"toast-{i}-{s:0.0}x-{bg.Item1}.png"), ImageFormat.Png);
                            }
                    i++;
                }
            using (var sheet = new Bitmap(560, 140))
            using (var g = Graphics.FromImage(sheet))
            {
                g.Clear(Color.FromArgb(0x20, 0x20, 0x20));
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                int x = 10;
                foreach (var size in new[] { 16, 24, 32 })
                    foreach (var on in new[] { false, true })
                    {
                        using (var glyph = Theme.TrayGlyph(size, on))
                        {
                            g.DrawImage(glyph, x, 10, size, size);
                            g.DrawImage(glyph, x, 50, size * 2.5f, size * 2.5f);
                        }
                        x += (int)(size * 2.5f) + 14;
                    }
                sheet.Save(Path.Combine(dir, "tray-glyphs.png"), ImageFormat.Png);
            }
        }
    }
}

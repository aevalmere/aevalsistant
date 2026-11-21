using System;
using System.IO;

namespace Aevalsistant
{
    static partial class Tests
    {
        // The agent command line, keep-awake holds, the CLAUDE.md section, and agent-sent cards.
        // mac/Tests/CoreTests.swift runs the same checks.
        static void AgentCommands()
        {
            Check(AgentRequest.ParseMinutes("2h") == 120 && AgentRequest.ParseMinutes("1h30m") == 90 && AgentRequest.ParseMinutes("45") == 45
                && AgentRequest.ParseMinutes("30m") == 30 && AgentRequest.ParseMinutes("on") == 0 && AgentRequest.ParseMinutes("0") == -1
                && AgentRequest.ParseMinutes("25h") == -1 && AgentRequest.ParseMinutes("abc") == -1 && AgentRequest.ParseMinutes("") == -1,
                "agent: durations");

            var awake = AgentRequest.Parse(new[] { "--awake", "2h", "render", "job" });
            Check(awake != null && awake.Action == "awake" && awake.Minutes == 120 && awake.Reason == "render job", "agent: --awake with a reason");
            Check(AgentRequest.Parse(new[] { "--awake", "off" })?.Action == "release", "agent: --awake off releases");
            var note = AgentRequest.Parse(new[] { "--notify", "Done", "All", "tests", "pass", "--needs-you" });
            Check(note != null && note.Action == "notify" && note.Title == "Done" && note.Detail == "All tests pass" && note.NeedsYou, "agent: --notify");
            Check(AgentRequest.Parse(new[] { "--notify" }) == null && AgentRequest.Parse(new[] { "--awake", "soon" }) == null
                && AgentRequest.Parse(new string[0]) == null && AgentRequest.Parse(new[] { "--dance" }) == null, "agent: bad commands are refused");
            var sleep = AgentRequest.Parse(new[] { "--agent", "codex", "--sleep" });
            Check(sleep != null && sleep.Action == "sleep" && sleep.Agent == "codex", "agent: --agent names the agent");

            note.Pwd = @"C:\code\aevalrena";
            note.Pid = 7;
            note.PidStart = 99;
            note.Hwnd = 42;
            var back = AgentRequest.From(note.ToJson());
            Check(back != null && back.Action == "notify" && back.Title == "Done" && back.Detail == "All tests pass" && back.NeedsYou
                && back.Pwd == @"C:\code\aevalrena" && back.Pid == 7 && back.PidStart == 99 && back.Hwnd == 42,
                "agent: a request survives the trip to the tray app");
            Check(AgentRequest.From("{\"cmd\":\"hello\"}") == null, "agent: other messages are not agent requests");

            var t0 = new DateTime(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);
            var holds = new AwakeHolds();
            holds.Add(new Hold { Key = "a", Label = "render", Since = t0, Until = t0.AddMinutes(30) });
            holds.Add(new Hold { Key = "b", Label = "build", Since = t0, Pid = 5 });
            holds.Add(new Hold { Key = "a", Label = "render", Since = t0, Until = t0.AddHours(2) });
            Check(holds.All.Count == 2 && AwakeHolds.Text(holds.All[1], t0) == "2h 0m left" && AwakeHolds.Text(holds.All[0], t0) == "until released",
                "holds: one per agent, the newest wins");
            Check(!holds.Sweep(t0.AddHours(1), h => true) && holds.Active, "holds: kept while time is left and the agent runs");
            Check(holds.Sweep(t0.AddHours(1), h => h.Pid != 5) && holds.All.Count == 1, "holds: an open-ended hold ends with its agent");
            Check(holds.Sweep(t0.AddHours(3), h => true) && !holds.Active, "holds: a timed hold runs out");
            holds.Add(new Hold { Key = "c", Since = t0 });
            Check(holds.Release("c") && !holds.Release("c"), "holds: release");

            var s = new Settings();
            string block = AgentGuide.Block("aeval", s);
            Check(block.StartsWith(AgentGuide.Start, StringComparison.Ordinal) && block.EndsWith(AgentGuide.End + "\n", StringComparison.Ordinal)
                && block.Contains("aeval --awake 2h") && block.Contains("aeval --sleep") && !block.Contains("--shutdown")
                && block.Contains("are automatic"), "guide: lists what agents may do");
            s.AgentPower = true;
            s.NotifyMode = 2;
            string decides = AgentGuide.Block("aeval", s);
            Check(decides.Contains("aeval --shutdown") && decides.Contains("lets you decide"), "guide: follows the settings");
            Check(AgentGuide.Block("aeval", new Settings { AgentGuide = false }) == "", "guide: off means no section");

            Check(AgentGuide.Apply(null, block) == block, "guide: a new file is just the section");
            Check(AgentGuide.Apply("# Mine\n", block) == "# Mine\n\n" + block, "guide: added after the user's own text");
            Check(AgentGuide.Apply("# Mine\n\n" + block, decides) == "# Mine\n\n" + decides, "guide: replaced in place");
            Check(AgentGuide.Apply("# Mine\n\n" + block + "\n# After\n", decides) == "# Mine\n\n# After\n\n" + decides, "guide: text after it is kept");
            Check(AgentGuide.Apply("# Mine\n\n" + block, "") == "# Mine\n" && AgentGuide.Apply(null, "") == null, "guide: removed cleanly");

            var cards = new Settings();
            var fromAgent = new ToastRequest { Kind = ToastKind.Done, FromAgent = true };
            bool ok = true;
            for (int m = 0; m < 4; m++) { cards.NotifyMode = m; ok &= cards.Wants(fromAgent) == (m != 3); }
            cards.NotifyMode = 2;
            Check(ok && !cards.Wants(new ToastRequest { Kind = ToastKind.Done }) && cards.Wants(new ToastRequest { Kind = ToastKind.NeedsYou }),
                "notify: agent cards show unless notifications are off; with agent decides, finish cards do not");

            string path = Path.Combine(Path.GetTempPath(), "aeval-agent-test.ini");
            new Settings { AgentAwake = false, AgentSleep = false, AgentPower = true, AgentGuide = false, AgentDecides = true, NotifyDone = false }.Save(path);
            var loaded = Settings.Load(path);
            File.Delete(path);
            Check(!loaded.AgentAwake && !loaded.AgentSleep && loaded.AgentPower && !loaded.AgentGuide && loaded.NotifyMode == 2,
                "settings: agent options survive a save and load");

            var book = new SessionBook();
            book.Apply(new HookEvent { Name = "UserPromptSubmit", SessionId = "me", Cwd = @"C:\code\a", Pid = 10, PidStart = 1 }, t0);
            book.Apply(new HookEvent { Name = "UserPromptSubmit", SessionId = "other", Cwd = @"C:\code\b", Pid = 20, PidStart = 2 }, t0);
            var me = book.Find(10, 1, "");
            Check(me?.Id == "me" && book.Find(0, 0, @"C:\code\B\")?.Id == "other", "agent: finds the asking session by process or folder");
            var blockers = book.BusyBlockers(10, 1, me.Id);
            Check(blockers.Count == 1 && blockers[0].Id == "other", "agent: its own work does not block a sleep it asked for");

            // The real pipe, under a scratch name so it never meets a running tray app.
            string pipe = "aevalsistant.test." + Guid.NewGuid().ToString("N");
            string got = null;
            var server = new System.Threading.Thread(() => CommandPipe.ServeOne(pipe, text => { got = text; return "Shown. é"; })) { IsBackground = true };
            server.Start();
            string answer = CommandPipe.Ask(pipe, note.ToJson(), 5000);
            server.Join(5000);
            Check(answer == "Shown. é" && AgentRequest.From(got)?.Title == "Done", "agent: a command and its answer cross the pipe");
            Check(CommandPipe.Ask("aevalsistant.test.nobody." + Guid.NewGuid().ToString("N"), "{}", 200) == null, "agent: no tray app, no answer");
        }
    }
}

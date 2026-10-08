using System;
using System.IO;
using System.Linq;

namespace Aevalsistant
{
    static partial class Tests
    {
        static HookEvent At(string name, string sid, string cwd, int pid, int outer = 0, string agentId = "", string agentType = "") =>
            new HookEvent { Name = name, SessionId = sid, Cwd = cwd, Pid = pid, PidStart = pid * 10, OuterPid = outer, OuterStart = outer * 10, AgentId = agentId, AgentType = agentType };

        // Subagents and sessions that another agent started are listed under their session, and
        // what happens in the background does not ask for a card unless settings say so.
        static void Hierarchy()
        {
            var t = new DateTime(2026, 10, 8, 9, 0, 0, DateTimeKind.Utc);
            var book = new SessionBook();
            book.Apply(At("UserPromptSubmit", "p", @"C:\code\aevalrena", 100), t);
            book.Apply(At("SubagentStart", "p", @"C:\code\aevalrena", 100, agentId: "a1", agentType: "Explore"), t.AddSeconds(1));
            book.Apply(At("SubagentStart", "p", @"C:\code\aevalrena", 100, agentId: "a2", agentType: "general-purpose"), t.AddSeconds(2));
            book.Apply(At("UserPromptSubmit", "c", @"C:\code\docs-build", 200, outer: 100), t.AddSeconds(3));
            book.Apply(At("UserPromptSubmit", "o", @"C:\code\kymarion", 300), t.AddSeconds(4));

            Check(book.ParentOf(book.Get("c")) == book.Get("p") && book.ParentOf(book.Get("p")) == null, "tree: a session started from another agent's process has that session as parent");
            var tree = book.Tree(null, nest: true);
            string Shape(System.Collections.Generic.List<TreeRow> rows) =>
                string.Join(" ", rows.Select(r => new string('>', r.Depth) + (r.Subagent != null ? r.Subagent.Type : r.Session.Project)));
            Check(Shape(tree) == "kymarion aevalrena >Explore >general-purpose >docs-build", "tree: subagents, then started sessions, nest under their session (" + Shape(tree) + ")");
            Check(Shape(book.Tree(null, nest: false)) == "kymarion docs-build aevalrena", "tree: nesting off lists every session flat (" + Shape(book.Tree(null, nest: false)) + ")");
            Check(Shape(book.Tree("p", nest: true)) == ">Explore >general-purpose >docs-build kymarion", "tree: the card's own session leads with its children");
            Check(Status.Text(book.Get("p").Subagents["a1"], t.AddMinutes(4).AddSeconds(1)) == "running 4m", "tree: subagent status");

            // Your own turn ending notifies; a turn the agent takes on its own afterwards is background.
            var done = book.Apply(At("Stop", "o", @"C:\code\kymarion", 300), t.AddMinutes(1));
            Check(done != null && !done.Background, "background: the turn you prompted is yours");
            var again = book.Apply(At("Stop", "o", @"C:\code\kymarion", 300), t.AddMinutes(2));
            Check(again != null && again.Background, "background: a second stop with no prompt in between is the agent on its own");
            book.Apply(At("UserPromptSubmit", "o", @"C:\code\kymarion", 300), t.AddMinutes(3));
            Check(!book.Apply(At("Stop", "o", @"C:\code\kymarion", 300), t.AddMinutes(4)).Background, "background: a new prompt makes the next stop yours again");

            var child = book.Apply(At("Stop", "c", @"C:\code\docs-build", 200, outer: 100), t.AddMinutes(1));
            Check(child != null && child.Background, "background: a session another agent started is background");
            var childAsk = book.Apply(new HookEvent { Name = "Notification", SessionId = "c", NotificationType = "permission_prompt", Pid = 200, PidStart = 2000, OuterPid = 100, OuterStart = 1000 }, t.AddMinutes(1));
            Check(childAsk != null && childAsk.Kind == ToastKind.NeedsYou && childAsk.Background, "background: its permission prompts are flagged too");

            var waiting = book.Apply(At("Stop", "p", @"C:\code\aevalrena", 100), t.AddMinutes(2));
            Check(waiting.Title == "aevalrena is waiting" && !waiting.Background && book.Get("p").Busy, "background: the parent's turn ends while its subagents run");
            Check(book.Apply(At("SubagentStop", "p", @"C:\code\aevalrena", 100, agentId: "a1"), t.AddMinutes(3)) == null, "background: one of two subagents finishing says nothing");
            var last = book.Apply(At("SubagentStop", "p", @"C:\code\aevalrena", 100, agentId: "a2"), t.AddMinutes(4));
            Check(last != null && last.Background && last.Title == "aevalrena finished" && !book.Get("p").Busy, "background: the last background subagent finishing is a background card");

            book.Apply(At("UserPromptSubmit", "p", @"C:\code\aevalrena", 100), t.AddMinutes(5));
            book.Apply(At("SubagentStart", "p", @"C:\code\aevalrena", 100, agentId: "a3"), t.AddMinutes(5));
            Check(book.Apply(At("SubagentStop", "p", @"C:\code\aevalrena", 100, agentId: "a3"), t.AddMinutes(6)) == null, "background: a subagent finishing mid-turn is part of the turn");

            var s = new Settings();
            var fg = new ToastRequest { Kind = ToastKind.Done };
            var bg = new ToastRequest { Kind = ToastKind.Done, Background = true };
            var bgAsk = new ToastRequest { Kind = ToastKind.NeedsYou, Background = true };
            Check(s.Wants(fg) && !s.Wants(bg) && s.Wants(bgAsk), "settings: by default background work only asks for you when it needs you");
            s.NotifyBackgroundDone = true;
            Check(s.Wants(bg), "settings: background finishes can be turned on");
            s.NotifyDone = false;
            Check(!s.Wants(fg) && !s.Wants(bg) && s.Wants(new ToastRequest { Kind = ToastKind.Info }), "settings: turning off finish cards also silences their background ones");
            Check(Math.Abs(new Settings { CardTime = 0 }.Seconds(ToastKind.Done) - 4.8) < 0.01 && new Settings().Seconds(ToastKind.NeedsYou) == 10, "settings: card time scales the defaults");

            string path = Path.Combine(Path.GetTempPath(), "aeval-settings-test.ini");
            var saved = new Settings { KeepAwake = false, Sound = false, CardTime = 2, ShowChildren = false, NotifyBackgroundDone = true };
            saved.AgentsOff.Add("cursor");
            saved.AgentsOff.Add("codex");
            saved.Save(path);
            var loaded = Settings.Load(path);
            File.Delete(path);
            Check(!loaded.KeepAwake && !loaded.Sound && loaded.CardTime == 2 && !loaded.ShowChildren && loaded.NotifyBackgroundDone
                && loaded.AgentsOff.SetEquals(new[] { "codex", "cursor" }) && loaded.NotifyNeedsYou && loaded.ExpandOnHover, "settings: new options survive a save and load");

            var env = HookEvent.FromEnvelope("{\"t\":1,\"agent\":\"claude\",\"event\":\"\",\"pwd\":\"\",\"host\":\"\",\"hwnd\":0,\"pid\":200,\"pidStart\":5,\"outer\":100,\"outerStart\":4,\"raw\":"
                + Json.Quote("{\"session_id\":\"x\",\"hook_event_name\":\"SubagentStart\",\"agent_id\":\"a\",\"agent_type\":\"Plan\"}") + "}");
            Check(env != null && env.OuterPid == 100 && env.OuterStart == 4 && env.AgentType == "Plan", "envelope: outer agent process and subagent type");
        }
    }
}

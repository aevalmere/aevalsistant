using System;
using System.IO;
using System.Linq;

namespace Aevalsistant
{
    static partial class Tests
    {
        // Watched apps, time tracking, reminders, and the newer notification filters. The Mac
        // side runs the same checks in mac/Tests/CoreTests.swift.
        static void Features()
        {
            Check(WatchMatch.Matches("blender", "Blender") && WatchMatch.Matches("blender", "blender.exe")
                && WatchMatch.Matches("blender", "blender-launcher") && WatchMatch.Matches("blender", "Blender.app")
                && !WatchMatch.Matches("blender", "blenderx") && !WatchMatch.Matches("blender", "myblender") && !WatchMatch.Matches("", "blender"),
                "apps: a watched name matches every part of that app and nothing else");

            var fresh = new Settings();
            Check(fresh.WatchApps.Count == 1 && fresh.WatchApps[0].Name == "blender" && fresh.WatchApps[0].On && fresh.WatchAppsOn,
                "apps: Blender is watched by default");

            string path = Path.Combine(Path.GetTempPath(), "aeval-features-test.ini");
            var saved = new Settings { WatchAppsOn = false, NotifyAway = true, MinTurn = 2, Remind = true, RemindAfter = 2 };
            saved.WatchApps.Add(new WatchedApp { Name = "obs64", Label = "OBS, Studio|x", On = false });
            saved.Save(path);
            var loaded = Settings.Load(path);
            Check(!loaded.WatchAppsOn && loaded.NotifyAway && loaded.MinTurn == 2 && loaded.Remind && loaded.RemindAfter == 2
                && loaded.WatchApps.Count == 2 && loaded.WatchApps[1].Name == "obs64" && loaded.WatchApps[1].Label == "OBS  Studio x" && !loaded.WatchApps[1].On,
                "settings: watched apps and notification options survive a save and load");
            File.WriteAllText(path, "watchApps=\n");
            Check(Settings.Load(path).WatchApps.Count == 0, "settings: an emptied app list stays empty");
            File.WriteAllText(path, "sound=0\n");
            Check(Settings.Load(path).WatchApps.Count == 1, "settings: older settings files get Blender");
            File.Delete(path);

            var mode = new Settings();
            Check(mode.NotifyMode == 0, "notify: always by default");
            mode.NotifyMode = 1;
            Check(!mode.NotifyDone && mode.NotifyNeedsYou && mode.NotifyMode == 1, "notify: only when agents need me");
            mode.NotifyMode = 2;
            Check(!mode.NotifyDone && !mode.NotifyNeedsYou && mode.NotifyMode == 2, "notify: never");

            var quick = new Settings { MinTurn = 1 };
            Check(!quick.Wants(new ToastRequest { Kind = ToastKind.Done, TurnSeconds = 30 })
                && quick.Wants(new ToastRequest { Kind = ToastKind.Done, TurnSeconds = 120 })
                && quick.Wants(new ToastRequest { Kind = ToastKind.Done })
                && quick.Wants(new ToastRequest { Kind = ToastKind.NeedsYou, TurnSeconds = 5 }),
                "notify: quick turns skip their finish card, needs-you cards always show");

            var t0 = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);
            var book = new SessionBook();
            book.Apply(Ev("UserPromptSubmit", at: 1), t0);
            var stop = book.Apply(Ev("Stop", at: 2), t0.AddSeconds(90));
            Check(stop != null && Math.Abs(stop.TurnSeconds - 90) < 0.01, "notify: a finish card knows how long its turn took");

            book.Apply(Ev("UserPromptSubmit", at: 3), t0.AddMinutes(2));
            book.Apply(Ev("Notification", type: "permission_prompt", msg: "Allow Bash?"), t0.AddMinutes(3));
            Check(book.Reminders(t0.AddMinutes(4), TimeSpan.FromMinutes(2)).Count == 0, "remind: not before the interval");
            var again = book.Reminders(t0.AddMinutes(5), TimeSpan.FromMinutes(2));
            Check(again.Count == 1 && again[0].Kind == ToastKind.NeedsYou && again[0].Title == "aevalrena still needs you"
                && again[0].Detail == "It has been waiting 2m.", "remind: a card again while it still needs you");
            Check(book.Reminders(t0.AddMinutes(6), TimeSpan.FromMinutes(2)).Count == 0
                && book.Reminders(t0.AddMinutes(7), TimeSpan.FromMinutes(2)).Count == 1, "remind: once per interval");
            book.Apply(Ev("Stop", at: 4), t0.AddMinutes(8));
            Check(book.Reminders(t0.AddMinutes(30), TimeSpan.FromMinutes(2)).Count == 0, "remind: stops once answered");

            var log = new TimeLog();
            Check(log.Tick(t0, "2026-11-20", new[] { "agent:claude" }) == 0, "time: the first tick only starts the clock");
            log.Tick(t0.AddSeconds(5), "2026-11-20", new[] { "agent:claude", "app:blender" });
            log.Tick(t0.AddMinutes(30), "2026-11-20", new[] { "app:blender" });
            Check(log.Today["agent:claude"] == 5 && log.Today["app:blender"] == 65, "time: a long gap counts as at most a minute");
            log.Tick(t0.AddHours(20), "2026-11-21", new[] { "app:blender" });
            Check(!log.Today.ContainsKey("agent:claude") && log.Today["app:blender"] == 60 && log.Total["app:blender"] == 125 && log.Total["agent:claude"] == 5,
                "time: a new day starts today's totals over and keeps the overall ones");
            string timePath = Path.Combine(Path.GetTempPath(), "aeval-time-test.ini");
            log.Save(timePath);
            var back = TimeLog.Load(timePath);
            File.Delete(timePath);
            Check(back.Day == "2026-11-21" && back.Today["app:blender"] == 60 && back.Total["agent:claude"] == 5, "time: survives a save and load");
            Check(TimeLog.Summary(back.Total, k => TimeKeys.Label(k, fresh.WatchApps)) == "Blender 2m", "time: summary skips anything under a minute");
            Check(TimeKeys.Label("agent:codex", fresh.WatchApps) == "Codex" && TimeKeys.Label("agent:chat", fresh.WatchApps) == "Chat apps",
                "time: agent labels");
            Check(Status.Duration(30) == "under a minute" && Status.Duration(600) == "10m" && Status.Duration(3900) == "1h 5m", "time: durations");

            var worked = new SessionBook();
            worked.Apply(Ev("UserPromptSubmit", at: 1), t0);
            worked.Credit(50);
            worked.Credit(50);
            worked.Apply(Ev("Stop", at: 2), t0.AddMinutes(1));
            worked.Credit(50);
            var s = worked.All.First();
            Check(s.Worked == 100 && Status.Worked(s) == "1m worked", "time: a session adds up only the time it was busy");
        }
    }
}

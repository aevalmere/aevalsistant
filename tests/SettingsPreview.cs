using System.Drawing.Imaging;
using System.IO;

namespace Aevalsistant
{
    static partial class Tests
    {
        // Builds the settings page the tray app shows, renders it with everything on, with the
        // keep-awake group closed, and with focus and hover states, at 100% and 150%. Also
        // checks how parent toggles open and close the rows under them.
        static void SettingsPreview(string dir)
        {
            Directory.CreateDirectory(dir);
            int awakeCalls = 0, stayCalls = 0;
            using (var w = new SettingsWindow())
            {
                w.Section("Keep awake");
                var awake = w.AddToggle("Keep the PC awake while agents work",
                    "Windows won't sleep until the last agent stops. The screen can still turn off on its usual timer.", true, v => awakeCalls++);
                var screen = w.AddToggle("Keep the screen on too", null, false, v => { }, awake);
                var lid = w.AddToggle("Keep working with the lid closed", "When the agents finish with the lid shut, the laptop goes to sleep.", false, v => { }, awake);

                w.Section("Notifications");
                var finish = w.AddToggle("Show a card when an agent finishes", null, true, v => { });
                w.AddToggle("Also when background agents finish", null, false, v => { }, finish);
                var needs = w.AddToggle("Show a card when an agent needs you", null, true, v => { });
                w.AddToggle("Also when a background agent needs you", null, true, v => { }, needs);
                var sound = w.AddToggle("Play a sound", null, true, v => { });
                w.AddButton("Play", () => { }, sound);
                w.AddToggle("Expand the card on hover to show the whole message", null, true, v => { });
                w.AddToggle("Alt+Tab jumps to the agent while the card is up", null, true, v => { });
                var stay = w.AddChoice("Card stays up", new[] { "Short", "Normal", "Long" }, 1, i => stayCalls++);
                w.AddButton("Show a test notification", () => { });

                w.Section("Agents");
                var agents = w.AddToggle("Connect coding agents", null, true, v => { });
                w.AddToggle("Claude Code", "Connected", true, v => { }, agents);
                w.AddToggle("Codex", "Connected. Run /hooks once in Codex to trust the hooks.", true, v => { }, agents);
                var cursor = w.AddToggle("Cursor", "Not connected", false, v => { }, agents);
                w.AddToggle("List subagents under their session", null, true, v => { });
                w.AddToggle("Watch Claude and ChatGPT desktop chats", null, true, v => { });

                w.Section("General");
                w.AddToggle("Start with Windows", null, true, v => { });
                w.AddToggle("Update automatically", null, true, v => { });
                w.AddButton("Check for updates", () => { });
                var status = w.AddNote("Up to date (1.4.0)");
                w.AddLink("Release notes", "https://github.com/aevalmere/aevalsistant/releases");
                w.AddLink("Report a problem", "https://github.com/aevalmere/aevalsistant/issues/new/choose");
                w.AddButton("Remove from this PC", () => { }, danger: true);

                Check(screen.Shown && lid.Shown && cursor.Shown, "settings: rows under an on toggle are shown");
                SaveSettings(w, dir, "settings-on");
                int fullHeight = w.ContentHeight;

                awake.On = false;
                Check(!screen.Shown && !lid.Shown && awakeCalls == 0, "settings: a parent turned off from code hides its rows without calling back");
                SaveSettings(w, dir, "settings-off");
                Check(w.ContentHeight < fullHeight, "settings: the page gets shorter when rows close");

                awake.Click();
                Check(awake.On && screen.Shown && lid.Shown && awakeCalls == 1, "settings: a click turns the parent on, shows its rows, and calls back once");

                stay.Selected = 2;
                Check(stay.Selected == 2 && stayCalls == 0, "settings: choosing from code does not call back");
                stay.Click(0);
                Check(stay.Selected == 0 && stayCalls == 1, "settings: a click on a segment calls back once");
                stay.Selected = 1;

                status.Text = "";
                Check(!status.Shown, "settings: an empty note is hidden");
                status.Text = "Up to date (1.4.0)";
                Check(status.Shown, "settings: a note with text is shown");

                int withNote = w.ContentHeight, rows = w.RowCount;
                lid.Note = null;
                Check(lid.Note == "" && w.ContentHeight < withNote && w.RowCount == rows, "settings: clearing a toggle's note drops its second line");
                lid.Note = "When the agents finish with the lid shut, the laptop goes to sleep.";
                Check(w.ContentHeight == withNote, "settings: setting the note again brings the line back");

                w.PreviewRings.Add(w.FindPart("Show a card when an agent finishes"));
                w.PreviewRings.Add(w.FindPart("Card stays up"));
                w.PreviewRings.Add(w.FindPart("Show a test notification"));
                w.PreviewRings.Add(w.FindPart("Report a problem"));
                w.FindPart("Play a sound").SetHover(true);
                w.FindPart("Check for updates").SetHover(true);
                w.FindPart("Release notes").SetHover(true);
                SaveSettings(w, dir, "settings-states");
            }

            using (var w = new SettingsWindow())
            {
                var a = w.AddToggle("A", null, true, v => { });
                var b = w.AddToggle("B", null, true, v => { }, a);
                var c = w.AddToggle("C", null, true, v => { }, b);
                w.AddButton("One", () => { }, b);
                w.AddButton("Two", () => { }, b);
                Check(w.RowCount == 4, "settings: buttons added one after another under the same toggle share a row");
                a.On = false;
                Check(!b.Shown && !c.Shown, "settings: turning off the top toggle hides two levels below it");
                a.On = true;
                Check(b.Shown && c.Shown, "settings: turning it back on restores both levels");
                b.On = false;
                Check(a.Shown && !c.Shown, "settings: turning off the middle toggle hides only the level below it");
            }
        }

        static void SaveSettings(SettingsWindow w, string dir, string name)
        {
            foreach (var s in new[] { 1f, 1.5f })
                using (var bmp = w.Snapshot(s))
                    bmp.Save(Path.Combine(dir, $"{name}-{s:0.0}x.png"), ImageFormat.Png);
        }
    }
}

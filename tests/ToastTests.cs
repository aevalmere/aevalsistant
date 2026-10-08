using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace Aevalsistant
{
    static partial class Tests
    {
        const string LongDetail = "Fixed the LAN lobby: clients now reconnect after the host restarts, and the lobby list no longer "
            + "shows rooms whose host has already left. The reconnect waits for the host's new address instead of retrying the old one, "
            + "so a restart on another port works too. I also added a test that kills the host mid-match and checks that both clients "
            + "land back in the same room with their loadouts intact. Two flaky tests in the matchmaking suite were timing out on slow "
            + "machines; they now wait on the server's ready signal. Nothing else changed.";

        // A session with two subagents, a background session started by another session, and a
        // subagent of the main notification's own session ahead of them all.
        static List<ToastRow> FamilyRows() => new List<ToastRow>
        {
            new ToastRow { SessionId = "top/a1", Name = "Explore", Status = "working 1m", State = RowState.Working, Depth = 1 },
            new ToastRow { SessionId = "a", Name = "aevalrena", Host = "VS Code", Status = "working 12m", State = RowState.Working },
            new ToastRow { SessionId = "a/x", Name = "Explore", Status = "working 3m", State = RowState.Working, Depth = 1 },
            new ToastRow { SessionId = "a/g", Name = "general-purpose", Status = "done 1m ago", State = RowState.Done, Depth = 1 },
            new ToastRow { SessionId = "k", Name = "kymarion", Host = "Terminal", Status = "needs you", State = RowState.NeedsYou },
            new ToastRow { SessionId = "d", Name = "docs-build", Host = "Terminal", Status = "working 40s", State = RowState.Working, Depth = 1 },
        };

        static ToastContent LongCard() => new ToastContent
        {
            Title = "gnce-site finished", Host = "VS Code", Detail = LongDetail, Kind = ToastKind.Done, Keycap = true, Rows = FamilyRows(),
        };

        static void ToastLayout()
        {
            var shortCard = new ToastContent { Title = "forge finished", Detail = "Tests pass.", Kind = ToastKind.Done, Keycap = true };
            var longCard = LongCard();
            var empty = new ToastContent { Title = "Aevalsistant is running", Kind = ToastKind.Info };
            Check(ToastArt.ExtraHeight(shortCard, 1) == 0 && ToastArt.ExtraHeight(shortCard, 1.5f) == 0 && ToastArt.ExtraHeight(empty, 1) == 0,
                "toast: a detail that fits on one line does not open");
            Check(ToastArt.ExtraHeight(longCard, 1) > 0 && ToastArt.ExtraHeight(longCard, 1.5f) > 0, "toast: a long detail opens");
            Check(ToastArt.Height(shortCard, 1, 1) == ToastArt.Height(shortCard, 1, 0), "toast: hovering a short detail keeps the height");
            var huge = new ToastContent { Title = "x", Detail = string.Join(" ", Enumerable.Repeat("reconnect", 400)), Keycap = true };
            Check(ToastArt.ExtraHeight(huge, 1) == (ToastArt.MaxLines - 1) * ToastArt.LineH, "toast: the detail stops at eight lines");
            var word = new ToastContent { Title = "x", Detail = "C:/" + new string('a', 300) + ".txt" };
            Check(ToastArt.ExtraHeight(word, 1) > 0 && ToastArt.ExtraHeight(word, 1) <= (ToastArt.MaxLines - 1) * ToastArt.LineH,
                "toast: one word longer than the line still wraps");

            foreach (var s in new[] { 1f, 1.25f, 1.5f, 2f })
            {
                float closed = ToastArt.Height(longCard, s, 0), half = ToastArt.Height(longCard, s, 0.5f), open = ToastArt.Height(longCard, s, 1);
                float extra = ToastArt.ExtraHeight(longCard, s);
                float list = ToastArt.ListTop + 2 * ToastArt.RowH + 4 * ToastArt.ChildH + ToastArt.ListBottom;
                Check(closed == ToastArt.HeadH + list && Math.Abs(open - closed - extra) <= 1 / s && closed < half && half < open
                    && Math.Abs(half - (closed + open) / 2) <= 1 / s, "toast: height runs from closed to open at " + s + "x");
                Check(ToastArt.PixelHeight(longCard, s) == (int)Math.Ceiling((open + ToastArt.Pad * 2) * s), "toast: the bitmap is sized for the open card at " + s + "x");

                foreach (var e in new[] { 0f, 1f })
                {
                    float x = (ToastArt.Pad + 200) * s, y = ToastArt.Pad + ToastArt.HeadH + extra * e + ToastArt.ListTop;
                    bool rows = true;
                    for (int i = 0; i < longCard.Rows.Count; i++)
                    {
                        float h = longCard.Rows[i].Depth > 0 ? ToastArt.ChildH : ToastArt.RowH;
                        rows &= ToastArt.HitTest(longCard, s, x, (y + h / 2) * s, e) == i;
                        y += h;
                    }
                    float headBottom = ToastArt.Pad + ToastArt.HeadH + extra * e;
                    Check(rows, "toast: hit test finds every row, children too, at " + s + "x expand " + e);
                    Check(ToastArt.HitTest(longCard, s, x, (headBottom - 2) * s, e) == ToastArt.HitHead
                        && ToastArt.HitTest(longCard, s, x, (headBottom + 2) * s, e) == ToastArt.HitCard
                        && ToastArt.HitTest(longCard, s, 2 * s, (ToastArt.Pad + 30) * s, e) == ToastArt.HitNone
                        && ToastArt.HitTest(longCard, s, x, (ToastArt.Pad + ToastArt.Height(longCard, s, e) + 4) * s, e) == ToastArt.HitNone,
                        "toast: head, list padding, and outside at " + s + "x expand " + e);
                }
            }
            Check(ToastArt.HitTest(longCard, 1, 200, ToastArt.Pad + ToastArt.HeadH + 20, 1) == ToastArt.HitHead
                && ToastArt.HitTest(longCard, 1, 200, ToastArt.Pad + ToastArt.HeadH + 20, 0) == 0, "toast: the open main row covers what was the first row");

            // the measurement is kept per scale and redone when the detail changes
            float at1 = ToastArt.ExtraHeight(longCard, 1);
            ToastArt.ExtraHeight(longCard, 1.5f);
            Check(ToastArt.ExtraHeight(longCard, 1) == at1, "toast: measurement survives a scale change");
            longCard.Detail = "Done.";
            Check(ToastArt.ExtraHeight(longCard, 1) == 0, "toast: a new detail is measured again");
        }

        static void ToastPreviews(string dir)
        {
            Directory.CreateDirectory(dir);
            var avatar = Theme.Resource("avatar.png");
            var family = LongCard();
            var fits = new ToastContent
            {
                Title = "kymarion needs you", Host = "Terminal", Detail = "Claude wants to run npm test", Kind = ToastKind.NeedsYou, Keycap = true,
                Rows = FamilyRows().Take(4).ToList(), Overflow = "2 more in the tray menu",
            };
            var shots = new List<(string name, ToastContent c, float expand, int hover)>
            {
                ("closed", family, 0, ToastArt.HitNone),
                ("half", family, 0.5f, ToastArt.HitHead),
                ("open", family, 1, 3),
                ("fits", fits, 0, 2),
            };
            foreach (var shot in shots)
                foreach (var s in new[] { 1f, 1.5f })
                    using (var card = ToastArt.Render(shot.c, s, shot.hover, avatar, shot.expand))
                        foreach (var bg in new[] { ("light", Color.FromArgb(0xF3, 0xF4, 0xF6)), ("dark", Color.FromArgb(0x1E, 0x1F, 0x22)) })
                            using (var frame = new Bitmap(card.Width + 40, card.Height + 20))
                            using (var g = Graphics.FromImage(frame))
                            {
                                g.Clear(bg.Item2);
                                g.DrawImage(card, 20, 0);
                                frame.Save(Path.Combine(dir, $"toast-{shot.name}-{s:0.0}x-{bg.Item1}.png"), ImageFormat.Png);
                            }

            // One frame of the card opening, the way the window draws it: into one reused bitmap.
            const float S = 1.5f;
            const int Frames = 60;
            using (var bmp = new Bitmap(ToastArt.PixelWidth(S), ToastArt.PixelHeight(family, S), PixelFormat.Format32bppPArgb))
            {
                ToastArt.Render(new ToastContent { Title = "x" }, S, ToastArt.HitNone, avatar, 0).Dispose();   // another size, so the next one blurs
                var watch = Stopwatch.StartNew();
                ToastArt.Render(bmp, family, S, ToastArt.HitHead, avatar, 0);   // the closed shadow is blurred here, once
                double first = watch.Elapsed.TotalMilliseconds, worst = 0;
                for (int i = 0; i < Frames; i++)
                {
                    watch.Restart();
                    ToastArt.Render(bmp, family, S, ToastArt.HitHead, avatar, (i + 1) / (float)Frames);
                    worst = Math.Max(worst, watch.Elapsed.TotalMilliseconds);
                }
                watch.Restart();
                for (int i = 0; i < Frames; i++) ToastArt.Render(bmp, family, S, ToastArt.HitHead, avatar, 1 - i / (float)Frames);
                Console.WriteLine($"toast: render at {S}x, {bmp.Width}x{bmp.Height}: {watch.Elapsed.TotalMilliseconds / Frames:0.00} ms per frame, worst {worst:0.00} ms"
                    + $" (the first, which blurs the shadow: {first:0} ms)");
            }
        }
    }
}

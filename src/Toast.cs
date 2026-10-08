using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using static Aevalsistant.Native;

namespace Aevalsistant
{
    // One of the other sessions, listed under the main notification.
    sealed class ToastRow
    {
        public string SessionId;
        public long Hwnd;
        public string Name = "";
        public string Host = "";
        public string Status = "";   // "working 4m", "done", "needs you"
        public RowState State;
        // 0: a session. 1: started by the nearest Depth-0 row above it (a subagent, or a
        // background session), or by the main notification's session when none comes first.
        public int Depth;
    }

    sealed class ToastContent
    {
        public string Title = "";
        public string Detail = "";
        public ToastKind Kind;
        public string Host = "";   // which app it is in: "VS Code", "Android Studio", "Claude"...
        public bool Keycap;        // show the Alt Tab hint (only when there is a window to jump to)
        public List<ToastRow> Rows = new List<ToastRow>();
        public string Overflow = "";   // "2 more in the tray menu" when the list is cut short

        // The detail broken into lines for one scale. Measuring needs a Graphics, and every
        // frame of the card opening asks for it again.
        internal ToastArt.Wrapped Wrap;
    }

    // Draws the card into a premultiplied bitmap. No window code here, so it can be rendered
    // to a PNG for review on any machine.
    static class ToastArt
    {
        public const float W = 420, HeadH = 64, RowH = 28, ChildH = 24, ListTop = 6, ListBottom = 8, Radius = 16, Pad = 22, TopGap = 16;
        public const float LineH = 16;   // pitch of the detail lines once the card opens
        public const int MaxRows = 6;    // rows drawn under the main notification, children included
        public const int MaxLines = 8;

        // Hover targets: the main notification, one of the listed rows, the card between
        // targets (list padding, the overflow line), or nothing.
        public const int HitNone = -2, HitHead = -1, HitCard = -3;

        static readonly StringFormat Typo = new StringFormat(StringFormat.GenericTypographic);

        static int RowCount(ToastContent c) => c.Rows.Count + (c.Overflow.Length > 0 ? 1 : 0);

        static float RowHeight(ToastContent c, int i) => i < c.Rows.Count && c.Rows[i].Depth > 0 ? ChildH : RowH;

        static float ListHeight(ToastContent c)
        {
            int n = RowCount(c);
            if (n == 0) return 0;
            float h = ListTop + ListBottom;
            for (int i = 0; i < n; i++) h += RowHeight(c, i);
            return h;
        }

        // How much taller the main row gets to show the whole detail: 0 when it fits on one line.
        public static float ExtraHeight(ToastContent c, float s) => (Wrap(c, s).Lines.Length - 1) * LineH;

        // Whole pixels, so the lower edge stays crisp and the shadow can be stretched by rows.
        static float Grow(ToastContent c, float s, float expand) =>
            (float)Math.Round(ExtraHeight(c, s) * s * Math.Max(0, Math.Min(1, expand)));

        public static float Height(ToastContent c, float s, float expand) => HeadH + Grow(c, s, expand) / s + ListHeight(c);

        public static int PixelWidth(float s) => (int)Math.Ceiling((W + Pad * 2) * s);

        // Always the open card's height, so a window keeps one surface while it opens and closes.
        public static int PixelHeight(ToastContent c, float s) => (int)Math.Ceiling((Height(c, s, 1) + Pad * 2) * s);

        // Which part of the card a point (in bitmap coordinates) is over.
        public static int HitTest(ToastContent c, float s, float x, float y, float expand)
        {
            var card = new RectangleF(Pad * s, Pad * s, W * s, Height(c, s, expand) * s);
            if (!card.Contains(x, y)) return HitNone;
            float ly = (y - card.Y - Grow(c, s, expand)) / s;   // as if the card were closed
            if (ly < HeadH) return HitHead;
            ly -= HeadH + ListTop;
            for (int i = 0; i < c.Rows.Count; i++)
            {
                float h = RowHeight(c, i);
                if (ly >= 0 && ly < h) return i;
                ly -= h;
            }
            return HitCard;
        }

        public static Bitmap Render(ToastContent c, float s, int hover, Image avatar, float expand)
        {
            var bmp = new Bitmap(PixelWidth(s), PixelHeight(c, s), PixelFormat.Format32bppPArgb);
            Render(bmp, c, s, hover, avatar, expand);
            return bmp;
        }

        // Into a bitmap of PixelWidth by PixelHeight, so a window can reuse one for every frame.
        public static void Render(Bitmap bmp, ToastContent c, float s, int hover, Image avatar, float expand)
        {
            var wrap = Wrap(c, s);
            float grow = Grow(c, s, expand), closedH = (HeadH + ListHeight(c)) * s;
            var card = new RectangleF(Pad * s, Pad * s, W * s, closedH + grow);
            // The avatar, title, and keycaps keep to the closed card's top band; the detail and
            // everything under it move down as the card opens.
            var band = new RectangleF(card.X, card.Y, card.Width, HeadH * s);
            PutShadow(bmp, new RectangleF(card.X, card.Y, card.Width, closedH), s, (int)grow);

            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;

                using (var path = Theme.Round(card, Radius * s))
                using (var fill = new SolidBrush(Theme.Paper))
                using (var edge = new Pen(hover != HitNone ? Theme.SlateMist : Color.FromArgb(200, Theme.PaperEdge), Math.Max(1f, s)))
                {
                    g.FillPath(fill, path);
                    g.DrawPath(edge, path);
                }

                // avatar
                float av = 40 * s;
                var avRect = new RectangleF(band.X + 12 * s, band.Y + (band.Height - av) / 2, av, av);
                if (avatar != null)
                {
                    using (var clip = new GraphicsPath())
                    {
                        clip.AddEllipse(avRect);
                        var state = g.Save();
                        g.SetClip(clip);
                        g.DrawImage(avatar, avRect);
                        g.Restore(state);
                    }
                }
                using (var ring = new Pen(Theme.PaperEdge, 1.2f * s)) g.DrawEllipse(ring, avRect);

                if (c.Kind != ToastKind.Info)
                {
                    float d = 10 * s;
                    var dot = new RectangleF(avRect.Right - d + 2.5f * s, avRect.Bottom - d + 2.5f * s, d, d);
                    using (var ring = new SolidBrush(Theme.Paper)) g.FillEllipse(ring, RectangleF.Inflate(dot, 2 * s, 2 * s));
                    using (var b = new SolidBrush(c.Kind == ToastKind.NeedsYou ? Theme.Blush : Theme.SlateSoft)) g.FillEllipse(b, dot);
                }

                // keycaps, laid out from the right edge inward
                float right = band.Right - 14 * s;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                if (c.Keycap)
                {
                    right = Key(g, "Tab", right, band, s) - 4 * s;
                    right = Key(g, "Alt", right, band, s) - 8 * s;
                }

                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                float x = card.X + TextLeft(s);
                var fmt = new StringFormat(Typo)
                {
                    FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter, LineAlignment = StringAlignment.Center,
                };
                using (var title = Theme.Semibold(13.5f * s))
                using (var host = Theme.Font(12.5f * s))
                using (var detail = Theme.Font(12f * s))
                using (var ink = new SolidBrush(Theme.Ink))
                using (var slate = new SolidBrush(Theme.Slate))
                {
                    bool oneLine = string.IsNullOrEmpty(c.Detail);
                    float ty = oneLine ? band.Y : band.Y + 12 * s, th = oneLine ? band.Height : 20 * s;
                    NameAndHost(g, c.Title, c.Host, title, host, ink, slate, fmt, x, ty, right - x - 4 * s, th, s);
                    if (!oneLine) DrawDetail(g, wrap, detail, slate, x, band.Y + 32 * s, grow, band.Bottom + grow - 14 * s, s);

                    if (RowCount(c) > 0) DrawRows(g, c, card, band.Bottom + grow, avRect, x, hover, s, fmt, ink, slate);
                }
            }
        }

        // From the card's left edge: past the avatar and its gap.
        static float TextLeft(float s) => (12 + 40 + 14) * s;

        // The detail: one line while the card is closed, the whole text as it opens. Lines past
        // the first are uncovered by the card's lower edge and fade in as they clear it.
        static void DrawDetail(Graphics g, Wrapped w, Font f, Brush slate, float x, float top, float grow, float bottom, float s)
        {
            var line = new StringFormat(Typo) { FormatFlags = Typo.FormatFlags | StringFormatFlags.NoWrap, LineAlignment = StringAlignment.Center };
            float lh = LineH * s, boxH = 18 * s, room = w.Width + 8 * s;
            if (w.Lines.Length == 1)
            {
                g.DrawString(w.Lines[0], f, slate, new RectangleF(x, top, room, boxH), line);
                return;
            }

            // The first line keeps its words in place and trades the ellipsis for the rest of
            // the line, so nothing jumps when the card starts to open.
            float k = Math.Min(1, grow / lh);
            g.DrawString(w.Prefix, f, slate, new RectangleF(x, top, room, boxH), line);
            var tail = new RectangleF(x + w.PrefixW, top, room, boxH);
            if (k < 1) using (var b = Faded(1 - k)) g.DrawString("…", f, b, tail, line);
            if (k > 0) using (var b = Faded(k)) g.DrawString(w.Lines[0].Substring(w.Prefix.Length), f, b, tail, line);
            if (grow <= 0) return;

            g.SetClip(new RectangleF(x, top, room, bottom - top));
            for (int i = 1; i < w.Lines.Length; i++)
            {
                float a = Math.Min(1, (grow - (i - 1) * lh) / lh);
                if (a <= 0) break;
                using (var b = Faded(a)) g.DrawString(w.Lines[i], f, b, new RectangleF(x, top + i * lh, room, boxH), line);
            }
            g.ResetClip();
        }

        static SolidBrush Faded(float a) => new SolidBrush(Color.FromArgb((int)Math.Round(255 * Math.Max(0, Math.Min(1, a))), Theme.Slate));

        internal sealed class Wrapped
        {
            public float Scale;
            public string Text;
            public bool Keycap;      // the keycaps take width from the detail line
            public float Width;      // the detail line, from the title's left edge to the keycaps
            public string[] Lines;   // at most MaxLines; the last ends in an ellipsis when the text runs on
            public string Prefix;    // the closed card shows Prefix and an ellipsis when there are more lines
            public float PrefixW;
        }

        // ExtraHeight is asked outside of any drawing, so measuring has a Graphics of its own.
        static Bitmap measureBitmap;
        static Graphics measurer;

        static Wrapped Wrap(ToastContent c, float s)
        {
            var w = c.Wrap;
            string detail = c.Detail ?? "";
            if (w != null && w.Scale == s && w.Keycap == c.Keycap && w.Text == detail) return w;
            if (measurer == null)
            {
                measureBitmap = new Bitmap(1, 1, PixelFormat.Format32bppPArgb);
                measurer = Graphics.FromImage(measureBitmap);
            }
            var g = measurer;
            w = new Wrapped { Scale = s, Text = detail, Keycap = c.Keycap };

            // the same right edge Render reaches by laying out the keycaps
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            float right = W * s - 14 * s;
            if (c.Keycap)
                using (var key = Theme.Semibold(10.5f * s))
                    right -= KeyWidth(g, "Tab", key, s) + 4 * s + KeyWidth(g, "Alt", key, s) + 8 * s;
            w.Width = right - TextLeft(s) - 4 * s;

            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            string text = string.Join(" ", detail.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
            var lines = new List<string>();
            using (var f = Theme.Font(12f * s))
            {
                // one line tall, so GDI+ reports how much of the rest fits on the next line
                var box = new SizeF(w.Width, f.GetHeight(g) * 1.5f);
                int at = 0;
                while (at < text.Length)
                {
                    string rest = text.Substring(at);
                    g.MeasureString(rest, f, box, Typo, out int fits, out _);
                    fits = Math.Max(1, Math.Min(fits, rest.Length));
                    if (lines.Count == MaxLines - 1 && fits < rest.Length)
                    {
                        lines.Add(Clip(g, f, rest.Substring(0, fits), w.Width) + "…");
                        break;
                    }
                    lines.Add(rest.Substring(0, fits).TrimEnd());
                    at += fits;
                    while (at < text.Length && text[at] == ' ') at++;
                }
                if (lines.Count == 0) lines.Add("");
                w.Lines = lines.ToArray();
                w.Prefix = lines.Count > 1 ? Clip(g, f, lines[0], w.Width) : lines[0];
                w.PrefixW = Measure(g, w.Prefix, f);
            }
            c.Wrap = w;
            return w;
        }

        // Drops whole words until the line and an ellipsis fit; a single long word loses letters
        // instead. A sentence's last stop goes too, or it reads as "intact.…".
        static string Clip(Graphics g, Font f, string line, float width)
        {
            string t = line.TrimEnd(ClipTrim);
            while (t.Length > 0 && Measure(g, t + "…", f) > width)
            {
                int cut = t.LastIndexOf(' ');
                t = (cut > 0 ? t.Substring(0, cut) : t.Substring(0, t.Length - 1)).TrimEnd(ClipTrim);
            }
            return t;
        }

        static readonly char[] ClipTrim = { ' ', '.', ',', ';', ':' };

        static float Measure(Graphics g, string text, Font f) => g.MeasureString(text, f, PointF.Empty, Typo).Width;

        // The other sessions: a hairline under the main notification, then one row each, with
        // the dot under the avatar and the text on the title's left edge. A child sits under its
        // session on a shorter row with a smaller dot, hung from the session's dot by an elbow.
        static void DrawRows(Graphics g, ToastContent c, RectangleF card, float top, RectangleF avRect, float x, int hover, float s,
            StringFormat fmt, Brush ink, Brush slate)
        {
            using (var line = new Pen(Theme.PaperEdge, Math.Max(1f, s)))
                g.DrawLine(line, card.X + 14 * s, top, card.Right - 14 * s, top);

            int n = RowCount(c);
            var ys = new float[n + 1];
            ys[0] = top + ListTop * s;
            for (int i = 0; i < n; i++) ys[i + 1] = ys[i] + RowHeight(c, i) * s;

            float dotX = avRect.X + avRect.Width / 2, childDotX = x + 3 * s, childX = x + 16 * s;
            if (hover >= 0 && hover < c.Rows.Count)
                using (var tint = new SolidBrush(Theme.PaperDeep))
                using (var p = Theme.Round(new RectangleF(card.X + 6 * s, ys[hover], card.Width - 12 * s, ys[hover + 1] - ys[hover]), 8 * s))
                    g.FillPath(tint, p);
            Connectors(g, c, ys, dotX, childDotX - 6 * s, avRect.Bottom + 4 * s, s);

            var right = new StringFormat(fmt) { Alignment = StringAlignment.Far };
            using (var name = Theme.Font(12.5f * s))
            using (var childName = Theme.Font(12f * s))
            using (var small = Theme.Font(11.5f * s))
            {
                for (int i = 0; i < n; i++)
                {
                    float y = ys[i], h = ys[i + 1] - ys[i];
                    var row = new RectangleF(card.X + 6 * s, y, card.Width - 12 * s, h);
                    if (i >= c.Rows.Count)
                    {
                        g.DrawString(c.Overflow, small, slate, new RectangleF(x, y, row.Right - x - 10 * s, row.Height), fmt);
                        break;
                    }
                    var r = c.Rows[i];
                    bool child = r.Depth > 0;
                    float d = (child ? 6 : 8) * s, cx = child ? childDotX : dotX, tx = child ? childX : x;
                    var dot = new RectangleF(cx - d / 2, y + (h - d) / 2, d, d);
                    if (r.State == RowState.Working)
                        using (var pen = new Pen(Theme.Slate, (child ? 1.2f : 1.4f) * s)) g.DrawEllipse(pen, RectangleF.Inflate(dot, -0.7f * s, -0.7f * s));
                    else
                        using (var b = new SolidBrush(r.State == RowState.NeedsYou ? Theme.Blush : Theme.SlateSoft)) g.FillEllipse(b, dot);

                    float statusW = g.MeasureString(r.Status, small, PointF.Empty, fmt).Width + 2 * s;
                    float statusRight = row.Right - 10 * s;
                    g.DrawString(r.Status, small, r.State == RowState.NeedsYou ? ink : slate,
                        new RectangleF(statusRight - statusW, y, statusW, row.Height), right);
                    NameAndHost(g, r.Name, r.Host, child ? childName : name, small, ink, slate, fmt, tx, y, statusRight - statusW - 12 * s - tx, row.Height, s);
                }
            }
        }

        // One vertical run per family from the session's dot (or from under the avatar, for
        // children of the main notification's session), with an arm out to each child's dot.
        static void Connectors(Graphics g, ToastContent c, float[] ys, float dotX, float armEnd, float headFrom, float s)
        {
            float pw = Math.Max(1, (float)Math.Round(s)), lx = Snap(dotX, pw), r = 4 * s;
            using (var path = new GraphicsPath())
            {
                float from = headFrom;
                for (int i = 0; i < c.Rows.Count; i++)
                {
                    float cy = Snap((ys[i] + ys[i + 1]) / 2, pw);
                    if (c.Rows[i].Depth == 0) { from = cy + 7 * s; continue; }   // clear of the session's dot
                    bool last = i + 1 == c.Rows.Count || c.Rows[i + 1].Depth == 0;
                    path.StartFigure();
                    if (last)
                    {
                        path.AddLine(lx, from, lx, cy - r);
                        path.AddArc(lx, cy - 2 * r, 2 * r, 2 * r, 180, -90);
                        path.AddLine(lx + r, cy, armEnd, cy);
                    }
                    else
                    {
                        path.AddLine(lx, from, lx, cy);
                        path.StartFigure();
                        path.AddLine(lx, cy, armEnd, cy);
                    }
                    from = cy;
                }
                if (path.PointCount == 0) return;
                using (var pen = new Pen(Theme.SlateMist, pw)) g.DrawPath(pen, path);
            }
        }

        // A stroke of whole-pixel width lands on whole pixels: odd widths on a pixel's center.
        static float Snap(float v, float width) => (int)width % 2 == 1 ? (float)Math.Floor(v) + 0.5f : (float)Math.Round(v);

        // "aevalrena finished  ·  VS Code". The name wins: the app shrinks first, to a stub.
        static void NameAndHost(Graphics g, string text, string hostName, Font main, Font sub, Brush mainBrush, Brush subBrush,
            StringFormat fmt, float x, float y, float width, float height, float s)
        {
            string hostText = string.IsNullOrEmpty(hostName) ? "" : "·  " + hostName;
            float gap = 5 * s;
            float textFull = g.MeasureString(text, main, PointF.Empty, fmt).Width + 2 * s;
            float hostFull = hostText.Length == 0 ? 0 : g.MeasureString(hostText, sub, PointF.Empty, fmt).Width + 2 * s;
            float hostW = 0, textW = Math.Min(textFull, width);
            if (hostFull > 0)
            {
                float room = width - gap - textFull;
                hostW = room >= hostFull ? hostFull : Math.Max(Math.Min(hostFull, 64 * s), room);
                textW = Math.Min(textFull, width - gap - hostW);
            }
            g.DrawString(text, main, mainBrush, new RectangleF(x, y, textW, height), fmt);
            if (hostW > 0) g.DrawString(hostText, sub, subBrush, new RectangleF(x + textW + gap, y + 0.5f * s, hostW, height), fmt);
        }

        // The shadow depends only on the closed card's size, so it is blurred once per size and
        // reused for hover redraws. Opening the card repeats a row from the middle, where the
        // rows of a blurred rounded rectangle are alike, instead of blurring again every frame.
        static byte[] shadow;
        static string shadowKey;

        static void PutShadow(Bitmap bmp, RectangleF closed, float s, int grow)
        {
            int w = bmp.Width, h = bmp.Height;
            string key = w + "x" + h + ":" + closed.Height + "@" + s;
            if (key != shadowKey)
            {
                using (var b = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
                {
                    DrawShadow(b, closed, Radius * s, 6 * s, 9 * s, 0.17f);
                    DrawShadow(b, closed, Radius * s, 1 * s, 1.5f * s, 0.10f);
                    var bd = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                    shadow = new byte[bd.Stride * h];
                    Marshal.Copy(bd.Scan0, shadow, 0, shadow.Length);
                    b.UnlockBits(bd);
                }
                shadowKey = key;
            }

            int mid = Math.Min(h - 1, (int)(closed.Y + closed.Height / 2));
            grow = Math.Max(0, Math.Min(grow, h - mid));
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            int stride = data.Stride;
            Marshal.Copy(shadow, 0, data.Scan0, mid * stride);
            for (int r = 0; r < grow; r++) Marshal.Copy(shadow, mid * stride, data.Scan0 + (mid + r) * stride, stride);
            Marshal.Copy(shadow, mid * stride, data.Scan0 + (mid + grow) * stride, (h - mid - grow) * stride);
            bmp.UnlockBits(data);
        }

        static float KeyWidth(Graphics g, string text, Font f, float s) =>
            Math.Max(g.MeasureString(text, f, PointF.Empty, Typo).Width + 12 * s, 24 * s);

        // A small keycap with a slightly heavier lower edge. Returns its left edge.
        static float Key(Graphics g, string text, float right, RectangleF band, float s)
        {
            using (var f = Theme.Semibold(10.5f * s))
            {
                float w = KeyWidth(g, text, f, s), h = 20 * s;
                var r = new RectangleF(right - w, band.Y + (band.Height - h) / 2, w, h);
                using (var path = Theme.Round(r, 5 * s))
                using (var bg = new SolidBrush(Theme.PaperDeep))
                using (var fg = new SolidBrush(Theme.Slate))
                {
                    using (var lip = Theme.Round(new RectangleF(r.X, r.Y + 1.5f * s, r.Width, r.Height), 5 * s))
                        using (var lipBrush = new SolidBrush(Theme.PaperEdge))
                            g.FillPath(lipBrush, lip);
                    g.FillPath(bg, path);
                    var fmt = new StringFormat(Typo) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString(text, f, fg, new RectangleF(r.X, r.Y + 0.5f * s, r.Width, r.Height), fmt);
                }
                return r.X;
            }
        }

        // Soft shadow: fill the card shape offset downward, box-blur the alpha three times
        // (close to a gaussian), and composite it under the card.
        static void DrawShadow(Bitmap target, RectangleF card, float radius, float dy, float blur, float strength)
        {
            int w = target.Width, h = target.Height;
            var alpha = new float[w * h];
            using (var mask = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(mask))
                using (var path = Theme.Round(new RectangleF(card.X, card.Y + dy, card.Width, card.Height), radius))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.FillPath(Brushes.Black, path);
                }
                var data = mask.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                var raw = new byte[data.Stride * h];
                Marshal.Copy(data.Scan0, raw, 0, raw.Length);
                mask.UnlockBits(data);
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        alpha[y * w + x] = raw[y * data.Stride + x * 4 + 3] / 255f;
            }
            int r = Math.Max(1, (int)Math.Round(blur / 1.7f));
            for (int pass = 0; pass < 3; pass++) { BoxH(alpha, w, h, r); BoxV(alpha, w, h, r); }

            var td = target.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
            var px = new byte[td.Stride * h];
            Marshal.Copy(td.Scan0, px, 0, px.Length);
            Color c = Theme.Shadow;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float a = alpha[y * w + x] * strength;
                    if (a <= 0.002f) continue;
                    int i = y * td.Stride + x * 4;
                    float inv = 1 - a;
                    // premultiplied "source over destination", shadow drawn first so it sits beneath
                    px[i + 0] = (byte)Math.Min(255, c.B * a + px[i + 0] * inv);
                    px[i + 1] = (byte)Math.Min(255, c.G * a + px[i + 1] * inv);
                    px[i + 2] = (byte)Math.Min(255, c.R * a + px[i + 2] * inv);
                    px[i + 3] = (byte)Math.Min(255, 255 * a + px[i + 3] * inv);
                }
            Marshal.Copy(px, 0, td.Scan0, px.Length);
            target.UnlockBits(td);
        }

        static void BoxH(float[] a, int w, int h, int r)
        {
            var row = new float[w];
            for (int y = 0; y < h; y++)
            {
                int o = y * w;
                float sum = 0;
                for (int x = -r; x <= r; x++) sum += a[o + Clamp(x, w)];
                for (int x = 0; x < w; x++)
                {
                    row[x] = sum / (2 * r + 1);
                    sum += a[o + Clamp(x + r + 1, w)] - a[o + Clamp(x - r, w)];
                }
                Array.Copy(row, 0, a, o, w);
            }
        }

        static void BoxV(float[] a, int w, int h, int r)
        {
            var col = new float[h];
            for (int x = 0; x < w; x++)
            {
                float sum = 0;
                for (int y = -r; y <= r; y++) sum += a[Clamp(y, h) * w + x];
                for (int y = 0; y < h; y++)
                {
                    col[y] = sum / (2 * r + 1);
                    sum += a[Clamp(y + r + 1, h) * w + x] - a[Clamp(y - r, h) * w + x];
                }
                for (int y = 0; y < h; y++) a[y * w + x] = col[y];
            }
        }

        static int Clamp(int v, int n) => v < 0 ? 0 : v >= n ? n - 1 : v;
    }

    static class Ease
    {
        // CSS cubic-bezier(x1, y1, x2, y2), solved for y at time x.
        public static double Bezier(double x1, double y1, double x2, double y2, double x)
        {
            if (x <= 0) return 0;
            if (x >= 1) return 1;
            double t = x;
            for (int i = 0; i < 8; i++)
            {
                double cx = Curve(x1, x2, t) - x;
                double dx = Slope(x1, x2, t);
                if (Math.Abs(cx) < 1e-5) break;
                if (Math.Abs(dx) < 1e-6) break;
                t -= cx / dx;
            }
            t = Math.Max(0, Math.Min(1, t));
            return Curve(y1, y2, t);
        }

        static double Curve(double a, double b, double t) => 3 * a * (1 - t) * (1 - t) * t + 3 * b * (1 - t) * t * t + t * t * t;
        static double Slope(double a, double b, double t) => 3 * a * (1 - t) * (1 - t) + 6 * (b - a) * (1 - t) * t + 3 * (1 - b) * t * t;

        public static double Enter(double x) => Bezier(0.16, 1, 0.3, 1, x);
        public static double Exit(double x) => Bezier(0.4, 0, 0.2, 1, x);
    }

    // The window: layered, never activated, topmost, excluded from screen capture.
    sealed class ToastWindow : NativeWindow, IDisposable
    {
        enum Phase { Hidden, Entering, Shown, Leaving }

        const double EnterMs = 360, ExitMs = 220, FadeOnlyEnterMs = 180, FadeOnlyExitMs = 160, OpenMs = 240, CloseMs = 200;
        static readonly TimeSpan UserAwayAfter = TimeSpan.FromSeconds(20);

        public event Action<int> Clicked;   // ToastArt.HitHead, or the index of a listed row, children included
        public event Action AltTabbed, Expired, Dismissed;

        // Hovering the card opens it to the whole detail when the detail runs past one line.
        public bool ExpandOnHover { get; set; } = true;

        readonly SynchronizationContext ui = SynchronizationContext.Current;
        readonly Bitmap avatar = Theme.Resource("avatar.png");
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly System.Windows.Forms.Timer dwell = new System.Windows.Forms.Timer { Interval = 100 };
        // A pointer crossing the card on its way somewhere else should not open it.
        readonly System.Windows.Forms.Timer openDelay = new System.Windows.Forms.Timer { Interval = 150 };
        readonly KeyHook keys = new KeyHook();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly Thread frames;

        ToastContent content;
        float scale = 1;
        int hover = ToastArt.HitNone;
        bool reduced;
        Bitmap frame;
        byte[] card; int cw, ch, stride;
        IntPtr memDc, dib, oldObj, bits;
        int ww, wh, winX, winY;
        int monitorLeft, monitorWidth;
        Phase phase = Phase.Hidden;
        double t0, fromY, fromA, toY, toA, curY, curA, duration;
        double expand, expandFrom, expandTo, expandT0, expandMs;   // 0 closed, 1 open
        double dwellLeft;
        volatile bool animating, resizing;   // the drop or fade; the card opening or closing
        int framePosted;

        public bool Showing => phase == Phase.Entering || phase == Phase.Shown;

        public ToastWindow()
        {
            CreateHandle(new CreateParams
            {
                Caption = "Aevalsistant",
                Style = WS_POPUP,
                ExStyle = WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
                X = -32000, Y = -32000, Width = 1, Height = 1,
            });
            // Screen capture and screen sharing see nothing here (Windows 10 2004+). Older
            // builds get a black box in captures instead of the content.
            if (!SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE)) SetWindowDisplayAffinity(Handle, WDA_MONITOR);

            dwell.Tick += (s, e) => Tick();
            openDelay.Tick += (s, e) =>
            {
                openDelay.Stop();
                if (hover == ToastArt.HitNone) return;
                ExpandTo(1);
                if (resizing) return;   // with reduced motion it opened at once, so draw it now
                Rebuild();
                if (!animating) Present(curY, curA);
            };
            keys.AltTab += () => AltTabbed?.Invoke();
            frames = new Thread(FrameLoop) { IsBackground = true, Name = "toast-frames" };
            frames.Start();
        }

        // seconds < 0 updates what is shown (a session changed state) without restarting the countdown.
        public void Show(ToastContent c, double seconds)
        {
            content = c;
            if (seconds >= 0 || phase == Phase.Hidden) dwellLeft = Math.Max(seconds, 5);
            reduced = ReducedMotion();
            if (phase == Phase.Hidden) Place();
            else if (ToastArt.PixelHeight(c, scale) != ch) Reflow();
            // An open card stays open across a list refresh while the new detail still needs the room.
            ExpandTo(hover != ToastArt.HitNone ? 1 : 0);
            Rebuild();

            if (c.Keycap) keys.Arm(); else keys.Disarm();

            if (phase == Phase.Shown || phase == Phase.Entering) { Present(curY, curA); return; }

            // From just above the screen edge: the surface is taller than the closed card, and
            // starting from its full height would make the drop faster.
            double startY = reduced ? FinalY : -Math.Ceiling((ToastArt.Height(c, scale, (float)expand) + ToastArt.Pad * 2) * scale);
            Animate(Phase.Entering, phase == Phase.Leaving ? curY : startY, phase == Phase.Leaving ? curA : 0,
                FinalY, 255, reduced ? FadeOnlyEnterMs : EnterMs);
            SetWindowPos(Handle, HWND_TOPMOST, winX, winY, ww, wh, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            ShowWindow(Handle, SW_SHOWNOACTIVATE);
        }

        public void Hide()
        {
            if (phase == Phase.Hidden || phase == Phase.Leaving) return;
            keys.Disarm();
            dwell.Stop();
            openDelay.Stop();
            Animate(Phase.Leaving, curY, curA, reduced ? curY : FinalY - 10 * scale, 0, reduced ? FadeOnlyExitMs : ExitMs);
        }

        double FinalY => Math.Round((ToastArt.TopGap - ToastArt.Pad) * scale);

        void Place()
        {
            GetCursorPos(out var pt);
            IntPtr mon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            GetMonitorInfo(mon, ref mi);
            try { scale = GetDpiForMonitor(mon, 0, out uint dx, out _) == 0 ? dx / 96f : 1f; }
            catch (DllNotFoundException) { scale = 1f; }   // Windows 7/8.0 have no shcore

            monitorLeft = mi.rcWork.Left;
            monitorWidth = mi.rcWork.Right - mi.rcWork.Left;
            winY = mi.rcWork.Top;   // the window is pinned to the top edge; the card slides inside it
            Reflow();
        }

        // Size the window for the current content, open: the list under the main row and the
        // length of the detail change its height. Opening and closing then only redraw.
        void Reflow()
        {
            cw = ToastArt.PixelWidth(scale);
            ch = ToastArt.PixelHeight(content, scale);
            ww = cw;
            wh = (int)FinalY + ch;
            winX = monitorLeft + (monitorWidth - cw) / 2;
            frame?.Dispose();
            frame = new Bitmap(cw, ch, PixelFormat.Format32bppPArgb);
            AllocSurface();
        }

        void Rebuild()
        {
            ToastArt.Render(frame, content, scale, hover, avatar, (float)expand);
            var data = frame.LockBits(new Rectangle(0, 0, cw, ch), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            stride = data.Stride;
            if (card == null || card.Length != stride * ch) card = new byte[stride * ch];
            Marshal.Copy(data.Scan0, card, 0, card.Length);
            frame.UnlockBits(data);
        }

        void AllocSurface()
        {
            FreeSurface();
            var bi = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER)), biWidth = ww, biHeight = -wh,   // top-down
                biPlanes = 1, biBitCount = 32,
            };
            memDc = CreateCompatibleDC(IntPtr.Zero);
            dib = CreateDIBSection(memDc, ref bi, 0, out bits, IntPtr.Zero, 0);
            oldObj = SelectObject(memDc, dib);
        }

        void FreeSurface()
        {
            if (memDc == IntPtr.Zero) return;
            SelectObject(memDc, oldObj);
            DeleteObject(dib);
            DeleteDC(memDc);
            memDc = dib = bits = IntPtr.Zero;
        }

        static readonly byte[] zeros = new byte[1 << 16];

        // One frame: the pre-rendered card copied into the window surface at row offset y.
        // The drop and fade change only position and opacity, so they redraw nothing.
        void Present(double y, double a)
        {
            curY = y; curA = a;
            int rowBytes = ww * 4;
            for (int off = 0, total = rowBytes * wh; off < total; off += zeros.Length)
                Marshal.Copy(zeros, 0, bits + off, Math.Min(zeros.Length, total - off));

            int dy = (int)Math.Round(y);
            for (int r = 0; r < ch; r++)
            {
                int target = r + dy;
                if (target < 0 || target >= wh) continue;
                Marshal.Copy(card, r * stride, bits + target * rowBytes, Math.Min(rowBytes, stride));
            }

            var pos = new POINT { X = winX, Y = winY };
            var size = new SIZE { CX = ww, CY = wh };
            var src = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = (byte)Math.Max(0, Math.Min(255, a)), AlphaFormat = AC_SRC_ALPHA };
            UpdateLayeredWindow(Handle, IntPtr.Zero, ref pos, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
        }

        void Animate(Phase next, double y0, double a0, double y1, double a1, double ms)
        {
            phase = next;
            fromY = y0; fromA = a0; toY = y1; toA = a1; duration = ms;
            t0 = clock.Elapsed.TotalMilliseconds;
            Present(y0, a0);
            animating = true;
            wake.Set();
        }

        // Opens (1) or closes (0) the card. A detail that fits on one line never opens.
        void ExpandTo(double to)
        {
            if (!ExpandOnHover || content == null || ToastArt.ExtraHeight(content, scale) <= 0)
            {
                expand = expandTo = 0;
                resizing = false;
                return;
            }
            if (to == expandTo) return;
            expandFrom = expand;
            expandTo = to;
            if (reduced) { expand = to; resizing = false; return; }
            // turning back part way covers only the distance already travelled
            expandMs = Math.Max(1, (to > expand ? OpenMs : CloseMs) * Math.Abs(to - expand));
            expandT0 = clock.Elapsed.TotalMilliseconds;
            resizing = true;
            wake.Set();
        }

        void Step()
        {
            if (resizing)
            {
                double p = Math.Min(1, (clock.Elapsed.TotalMilliseconds - expandT0) / expandMs);
                expand = expandFrom + (expandTo - expandFrom) * (expandTo > expandFrom ? Ease.Enter(p) : Ease.Exit(p));
                if (p >= 1) resizing = false;
                // The rows slide under a cursor that is standing still, so the row under it
                // changes without a mouse move.
                if (hover != ToastArt.HitNone)
                {
                    int under = CursorTarget();
                    if (under != ToastArt.HitNone) hover = under;
                }
                Rebuild();
                if (!animating) Present(curY, curA);
            }
            if (!animating) return;
            double t = Math.Min(1, (clock.Elapsed.TotalMilliseconds - t0) / duration);
            if (phase == Phase.Entering)
            {
                double e = Ease.Enter(t);
                // opacity leads position: fully opaque by 55% of the drop
                Present(fromY + (toY - fromY) * e, fromA + (toA - fromA) * Ease.Enter(Math.Min(1, t / 0.55)));
            }
            else
            {
                double e = Ease.Exit(t);
                Present(fromY + (toY - fromY) * e, fromA + (toA - fromA) * e);
            }
            if (t < 1) return;

            animating = false;
            if (phase == Phase.Entering) { phase = Phase.Shown; dwell.Start(); }
            else
            {
                phase = Phase.Hidden;
                ShowWindow(Handle, SW_HIDE);
                hover = ToastArt.HitNone;
                expand = expandTo = 0;
                resizing = false;
            }
        }

        void FrameLoop()
        {
            while (true)
            {
                wake.WaitOne();
                while (animating || resizing)
                {
                    // Paced by the compositor so every step lands on a real frame.
                    if (DwmFlush() != 0) Thread.Sleep(8);
                    if (Interlocked.Exchange(ref framePosted, 1) == 0)
                        ui.Post(_ => { framePosted = 0; Step(); }, null);
                }
            }
        }

        void Tick()
        {
            if (phase != Phase.Shown) { dwell.Stop(); return; }
            // The countdown only runs while someone is at the keyboard, so a toast that fires
            // while you are away is still there when you come back.
            if (hover != ToastArt.HitNone || SinceLastInput() > UserAwayAfter) return;
            dwellLeft -= dwell.Interval / 1000.0;
            if (dwellLeft <= 0) Expired?.Invoke();
        }

        int Hit(IntPtr lParam)
        {
            if (content == null) return ToastArt.HitNone;
            int x = (short)(lParam.ToInt64() & 0xFFFF), y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
            return ToastArt.HitTest(content, scale, x, (float)(y - curY), (float)expand);
        }

        int CursorTarget()
        {
            GetCursorPos(out var pt);
            return ToastArt.HitTest(content, scale, pt.X - winX, (float)(pt.Y - winY - curY), (float)expand);
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_MOUSEACTIVATE:
                    m.Result = new IntPtr(MA_NOACTIVATE);
                    return;
                case WM_SETCURSOR:
                    SetCursor(LoadCursor(IntPtr.Zero, IDC_HAND));
                    m.Result = new IntPtr(1);
                    return;
                case WM_MOUSEMOVE:
                    SetHover(Hit(m.LParam));
                    return;
                case WM_MOUSELEAVE:
                    SetHover(ToastArt.HitNone);
                    return;
                case WM_LBUTTONUP:
                    int target = Hit(m.LParam);
                    if (Showing && (target == ToastArt.HitHead || target >= 0)) Clicked?.Invoke(target);
                    return;
                case WM_RBUTTONUP:
                    if (Showing) Dismissed?.Invoke();
                    return;
            }
            base.WndProc(ref m);
        }

        void SetHover(int target)
        {
            if (target == hover || content == null) return;
            bool entering = hover == ToastArt.HitNone;
            hover = target;
            if (entering && target != ToastArt.HitNone)
            {
                var tme = new TRACKMOUSEEVENT { cbSize = Marshal.SizeOf(typeof(TRACKMOUSEEVENT)), dwFlags = TME_LEAVE, hwndTrack = Handle };
                TrackMouseEvent(ref tme);
            }
            // Anywhere on the card opens it, not only the main row, once the pointer settles.
            if (target == ToastArt.HitNone) { openDelay.Stop(); ExpandTo(0); }
            else if (entering) openDelay.Start();
            if (resizing) return;   // the next frame draws the new hover as well
            Rebuild();
            if (!animating) Present(curY, curA);
        }

        public void Dispose()
        {
            keys.Disarm();
            dwell.Dispose();
            openDelay.Dispose();
            FreeSurface();
            frame?.Dispose();
            DestroyHandle();
        }
    }

    // Alt+Tab is captured only while a toast with a target is on screen; otherwise the
    // hook is not installed at all.
    sealed class KeyHook
    {
        public event Action AltTab;
        readonly LowLevelKeyboardProc proc;
        readonly SynchronizationContext ui = SynchronizationContext.Current;
        IntPtr hook;
        bool swallowing;

        public KeyHook() { proc = Callback; }

        public void Arm()
        {
            if (hook != IntPtr.Zero) return;
            hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, Marshal.GetHINSTANCE(typeof(KeyHook).Module), 0);
        }

        public void Disarm()
        {
            if (hook == IntPtr.Zero) return;
            UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
            swallowing = false;
        }

        IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                var k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                int msg = wParam.ToInt32();
                bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                if (k.vkCode == VK_TAB && (k.flags & LLKHF_INJECTED) == 0)
                {
                    bool alt = (k.flags & LLKHF_ALTDOWN) != 0 || msg == WM_SYSKEYDOWN || (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
                    bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                    if (down && alt && !ctrl)
                    {
                        if (!swallowing)
                        {
                            swallowing = true;
                            TapUnassignedKey();
                            ui.Post(_ => AltTab?.Invoke(), null);
                        }
                        return new IntPtr(1);
                    }
                    if (!down && swallowing)
                    {
                        swallowing = false;
                        return new IntPtr(1);
                    }
                }
            }
            return CallNextHookEx(hook, code, wParam, lParam);
        }
    }
}

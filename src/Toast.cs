using System;
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
    }

    sealed class ToastContent
    {
        public string Title = "";
        public string Detail = "";
        public ToastKind Kind;
        public string Host = "";   // which app it is in: "VS Code", "Android Studio", "Claude"...
        public bool Keycap;        // show the Alt Tab hint (only when there is a window to jump to)
        public System.Collections.Generic.List<ToastRow> Rows = new System.Collections.Generic.List<ToastRow>();
        public string Overflow = "";   // "2 more in the tray menu" when the list is cut short
    }

    // Draws the card into a premultiplied bitmap. No window code here, so it can be rendered
    // to a PNG for review on any machine.
    static class ToastArt
    {
        public const float W = 420, HeadH = 64, RowH = 28, ListTop = 6, ListBottom = 8, Radius = 16, Pad = 22, TopGap = 16;
        public const int MaxRows = 6;

        // Hover targets: the main notification, one of the listed rows, or nothing.
        public const int HitNone = -2, HitHead = -1;

        static int RowCount(ToastContent c) => c.Rows.Count + (c.Overflow.Length > 0 ? 1 : 0);

        public static float Height(ToastContent c) =>
            HeadH + (RowCount(c) > 0 ? ListTop + RowCount(c) * RowH + ListBottom : 0);

        public static int PixelHeight(ToastContent c, float s) => (int)Math.Ceiling((Height(c) + Pad * 2) * s);

        // Which part of the card a point (in bitmap coordinates) is over.
        public static int HitTest(ToastContent c, float s, float x, float y)
        {
            var card = new RectangleF(Pad * s, Pad * s, W * s, Height(c) * s);
            if (!card.Contains(x, y)) return HitNone;
            if (y < card.Y + HeadH * s) return HitHead;
            int i = (int)Math.Floor((y - card.Y - (HeadH + ListTop) * s) / (RowH * s));
            return i >= 0 && i < c.Rows.Count ? i : HitNone;
        }

        public static Bitmap Render(ToastContent c, float s, int hover, Image avatar)
        {
            int cw = (int)Math.Ceiling((W + Pad * 2) * s), ch = PixelHeight(c, s);
            var bmp = new Bitmap(cw, ch, PixelFormat.Format32bppPArgb);
            var card = new RectangleF(Pad * s, Pad * s, W * s, Height(c) * s);
            var head = new RectangleF(card.X, card.Y, card.Width, HeadH * s);

            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.DrawImageUnscaled(Shadow(cw, ch, card, s), 0, 0);
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
                var avRect = new RectangleF(head.X + 12 * s, head.Y + (head.Height - av) / 2, av, av);
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
                float right = head.Right - 14 * s;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                if (c.Keycap)
                {
                    right = Key(g, "Tab", right, head, s) - 4 * s;
                    right = Key(g, "Alt", right, head, s) - 8 * s;
                }

                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                float x = avRect.Right + 14 * s;
                var fmt = new StringFormat(StringFormat.GenericTypographic)
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
                    float ty = oneLine ? head.Y : head.Y + 12 * s, th = oneLine ? head.Height : 20 * s;
                    NameAndHost(g, c.Title, c.Host, title, host, ink, slate, fmt, x, ty, right - x - 4 * s, th, s);
                    if (!oneLine) g.DrawString(c.Detail, detail, slate, new RectangleF(x, head.Y + 32 * s, right - x - 4 * s, 18 * s), fmt);

                    if (RowCount(c) > 0) DrawRows(g, c, card, avRect, x, hover, s, fmt, ink, slate);
                }
            }
            return bmp;
        }

        // The other sessions: a hairline under the main notification, then one row each,
        // with the dot under the avatar and the text on the title's left edge.
        static void DrawRows(Graphics g, ToastContent c, RectangleF card, RectangleF avRect, float x, int hover, float s,
            StringFormat fmt, Brush ink, Brush slate)
        {
            float top = card.Y + HeadH * s;
            using (var line = new Pen(Theme.PaperEdge, Math.Max(1f, s)))
                g.DrawLine(line, card.X + 14 * s, top, card.Right - 14 * s, top);

            float dotX = avRect.X + avRect.Width / 2;
            var right = new StringFormat(fmt) { Alignment = StringAlignment.Far };
            using (var name = Theme.Font(12.5f * s))
            using (var small = Theme.Font(11.5f * s))
            using (var tint = new SolidBrush(Theme.PaperDeep))
            {
                for (int i = 0; i < RowCount(c); i++)
                {
                    float y = top + (ListTop + i * RowH) * s;
                    var row = new RectangleF(card.X + 6 * s, y, card.Width - 12 * s, RowH * s);
                    if (i >= c.Rows.Count)
                    {
                        g.DrawString(c.Overflow, small, slate, new RectangleF(x, y, row.Right - x - 10 * s, row.Height), fmt);
                        break;
                    }
                    var r = c.Rows[i];
                    if (hover == i)
                        using (var p = Theme.Round(row, 8 * s)) g.FillPath(tint, p);

                    float d = 8 * s;
                    var dot = new RectangleF(dotX - d / 2, y + (row.Height - d) / 2, d, d);
                    if (r.State == RowState.Working)
                        using (var pen = new Pen(Theme.Slate, 1.4f * s)) g.DrawEllipse(pen, RectangleF.Inflate(dot, -0.7f * s, -0.7f * s));
                    else
                        using (var b = new SolidBrush(r.State == RowState.NeedsYou ? Theme.Blush : Theme.SlateSoft)) g.FillEllipse(b, dot);

                    float statusW = g.MeasureString(r.Status, small, PointF.Empty, fmt).Width + 2 * s;
                    float statusRight = row.Right - 10 * s;
                    g.DrawString(r.Status, small, r.State == RowState.NeedsYou ? ink : slate,
                        new RectangleF(statusRight - statusW, y, statusW, row.Height), right);
                    NameAndHost(g, r.Name, r.Host, name, small, ink, slate, fmt, x, y, statusRight - statusW - 12 * s - x, row.Height, s);
                }
            }
        }

        // "aevalrena finished  ·  VS Code". The name wins: the app shrinks first, to a stub.
        static void NameAndHost(Graphics g, string text, string hostName, Font main, Font sub, Brush mainBrush, Brush subBrush,
            StringFormat fmt, float x, float y, float width, float height, float s)
        {
            string hostText = string.IsNullOrEmpty(hostName) ? "" : "\u00B7  " + hostName;
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

        // The shadow depends only on the card size, so it is blurred once per size and reused
        // for hover redraws.
        static Bitmap shadow;
        static string shadowKey;

        static Bitmap Shadow(int cw, int ch, RectangleF card, float s)
        {
            string key = cw + "x" + ch + "@" + s;
            if (key == shadowKey) return shadow;
            shadow?.Dispose();
            shadow = new Bitmap(cw, ch, PixelFormat.Format32bppPArgb);
            DrawShadow(shadow, card, Radius * s, 6 * s, 9 * s, 0.17f);
            DrawShadow(shadow, card, Radius * s, 1 * s, 1.5f * s, 0.10f);
            shadowKey = key;
            return shadow;
        }

        // A small keycap with a slightly heavier lower edge. Returns its left edge.
        static float Key(Graphics g, string text, float right, RectangleF head, float s)
        {
            using (var f = Theme.Semibold(10.5f * s))
            {
                var size = g.MeasureString(text, f, PointF.Empty, StringFormat.GenericTypographic);
                float w = Math.Max(size.Width + 12 * s, 24 * s), h = 20 * s;
                var r = new RectangleF(right - w, head.Y + (head.Height - h) / 2, w, h);
                using (var path = Theme.Round(r, 5 * s))
                using (var bg = new SolidBrush(Theme.PaperDeep))
                using (var fg = new SolidBrush(Theme.Slate))
                {
                    using (var lip = Theme.Round(new RectangleF(r.X, r.Y + 1.5f * s, r.Width, r.Height), 5 * s))
                        using (var lipBrush = new SolidBrush(Theme.PaperEdge))
                            g.FillPath(lipBrush, lip);
                    g.FillPath(bg, path);
                    var fmt = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
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

        const double EnterMs = 360, ExitMs = 220, FadeOnlyEnterMs = 180, FadeOnlyExitMs = 160;
        static readonly TimeSpan UserAwayAfter = TimeSpan.FromSeconds(20);

        public event Action<int> Clicked;   // ToastArt.HitHead, or the index of a listed session
        public event Action AltTabbed, Expired, Dismissed;

        readonly SynchronizationContext ui = SynchronizationContext.Current;
        readonly Bitmap avatar = Theme.Resource("avatar.png");
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly System.Windows.Forms.Timer dwell = new System.Windows.Forms.Timer { Interval = 100 };
        readonly KeyHook keys = new KeyHook();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly Thread frames;

        ToastContent content;
        float scale = 1;
        int hover = ToastArt.HitNone;
        bool reduced;
        byte[] card; int cw, ch, stride;
        IntPtr memDc, dib, oldObj, bits;
        int ww, wh, winX, winY;
        int monitorLeft, monitorWidth;
        Phase phase = Phase.Hidden;
        double t0, fromY, fromA, toY, toA, curY, curA, duration;
        double dwellLeft;
        volatile bool animating;
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
            Rebuild();

            if (c.Keycap) keys.Arm(); else keys.Disarm();

            if (phase == Phase.Shown || phase == Phase.Entering) { Present(curY, curA); return; }

            double startY = reduced ? FinalY : -ch;
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

        // Size the window for the current content; the list under the main row changes its height.
        void Reflow()
        {
            cw = (int)Math.Ceiling((ToastArt.W + ToastArt.Pad * 2) * scale);
            ch = ToastArt.PixelHeight(content, scale);
            ww = cw;
            wh = (int)FinalY + ch;
            winX = monitorLeft + (monitorWidth - cw) / 2;
            AllocSurface();
        }

        void Rebuild()
        {
            using (var bmp = ToastArt.Render(content, scale, hover, avatar))
            {
                var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                stride = data.Stride;
                card = new byte[stride * bmp.Height];
                Marshal.Copy(data.Scan0, card, 0, card.Length);
                bmp.UnlockBits(data);
            }
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
        // Only position and opacity change between frames, so nothing is redrawn.
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

        void Step()
        {
            if (!animating) return;
            double p = Math.Min(1, (clock.Elapsed.TotalMilliseconds - t0) / duration);
            if (phase == Phase.Entering)
            {
                double e = Ease.Enter(p);
                // opacity leads position: fully opaque by 55% of the drop
                Present(fromY + (toY - fromY) * e, fromA + (toA - fromA) * Ease.Enter(Math.Min(1, p / 0.55)));
            }
            else
            {
                double e = Ease.Exit(p);
                Present(fromY + (toY - fromY) * e, fromA + (toA - fromA) * e);
            }
            if (p < 1) return;

            animating = false;
            if (phase == Phase.Entering) { phase = Phase.Shown; dwell.Start(); }
            else
            {
                phase = Phase.Hidden;
                ShowWindow(Handle, SW_HIDE);
                hover = ToastArt.HitNone;
            }
        }

        void FrameLoop()
        {
            while (true)
            {
                wake.WaitOne();
                while (animating)
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
            return ToastArt.HitTest(content, scale, x, (float)(y - curY));
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
                    if (Showing && target != ToastArt.HitNone) Clicked?.Invoke(target);
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
            Rebuild();
            if (!animating) Present(curY, curA);
        }

        public void Dispose()
        {
            keys.Disarm();
            dwell.Dispose();
            FreeSurface();
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

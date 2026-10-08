using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Aevalsistant
{
    // A toggle on the settings page, kept by the tray app to update the row while the window is
    // open. Setting On from code never calls the row's callback, so the app can mirror its own
    // state into the page without a feedback loop.
    sealed class Toggle
    {
        internal readonly SettingsWindow.ToggleRow Row;
        internal Toggle(SettingsWindow.ToggleRow row) { Row = row; }

        public bool On { get => Row.On; set => Row.Set(value, notify: false); }

        // The second line under the label: a description, or live status such as "Connected".
        public string Note { get => Row.NoteText; set => Row.SetNote(value); }

        // The path a click or Space takes, for tests.
        internal void Click() => Row.Set(!Row.On, notify: true);
        internal bool Shown => Row.Shown;
    }

    sealed class Choice
    {
        internal readonly SettingsWindow.ChoiceRow Row;
        internal Choice(SettingsWindow.ChoiceRow row) { Row = row; }

        public int Selected { get => Row.Selected; set => Row.Select(value, notify: false); }

        internal void Click(int index) => Row.Select(index, notify: true);
        internal bool Shown => Row.Shown;
    }

    sealed class Note
    {
        internal readonly SettingsWindow.NoteRow Row;
        internal Note(SettingsWindow.NoteRow row) { Row = row; }

        public string Text { get => Row.Body; set => Row.SetBody(value); }

        internal bool Shown => Row.Shown;
    }

    // The settings page. The tray app adds rows top to bottom; a row added under a toggle is
    // indented and shown only while that toggle, and every toggle above it, is on. Changes apply
    // at once, as in Windows 11 Settings, so there is no OK or Cancel.
    sealed class SettingsWindow : Form
    {
        // Logical pixels at 96 DPI; Px() converts to the window's current DPI.
        const int PageWidth = 480, Edge = 12, Pad = 12, Step = 24, RowMin = 40, RowPad = 12, NoteGap = 0, Radius = 6;
        const int SwitchW = 36, SwitchH = 20, ButtonH = 32, ButtonPad = 16, SegmentH = 28, SegmentPad = 8, LinkH = 28, LinkPad = 8, RingPad = 4, StripW = 12;

        const int WM_DPICHANGED = 0x02E0, WM_NCHITTEST = 0x84, WM_GETMINMAXINFO = 0x24;
        const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17, HTBORDER = 18;
        const int DWMWA_BORDER_COLOR = 34, DWMWA_CAPTION_COLOR = 35, DWMWA_TEXT_COLOR = 36;
        const int WS_EX_COMPOSITED = 0x02000000;

        const TextFormatFlags Keep = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix
            | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform;
        internal const TextFormatFlags Wrap = Keep | TextFormatFlags.WordBreak;
        internal const TextFormatFlags OneLine = Keep | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
        internal const TextFormatFlags Centered = OneLine | TextFormatFlags.HorizontalCenter;

        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);

        readonly List<Item> items = new List<Item>();
        readonly Viewport view;
        readonly Page page;
        readonly ScrollStrip strip;
        readonly Icon appIcon;
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly Timer frames = new Timer { Interval = 15 };
        readonly HashSet<Part> moving = new HashSet<Part>();
        readonly Glide scroll = new Glide(200, 0, Ease.Enter);
        int dpi, contentHeight;
        bool settleLayout, settleScroll, quietFocus;

        internal Font Body, Small, Head, Underlined;
        internal readonly HashSet<Control> PreviewRings = new HashSet<Control>();

        public SettingsWindow()
        {
            Text = "Aevalsistant settings";
            appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Icon = appIcon;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Paper;

            // The window is created on the monitor the tray icon was clicked on, at that
            // monitor's DPI, so the first layout is already right.
            var at = Cursor.Position;
            var area = Screen.FromPoint(at).WorkingArea;
            Location = new Point(area.X + area.Width / 4, area.Y + area.Height / 8);
            dpi = MonitorDpi(at);
            MakeFonts();

            page = new Page(this);
            strip = new ScrollStrip(this) { Visible = false };
            view = new Viewport(this) { Dock = DockStyle.Fill };
            view.Controls.Add(page);
            view.Controls.Add(strip);
            strip.BringToFront();
            Controls.Add(view);
            ClientSize = new Size(Px(PageWidth), Px(480));
            frames.Tick += (s, e) => Frame();
        }

        public void Section(string title) => Add(new HeaderRow(this, title), null);

        public Toggle AddToggle(string label, string note, bool on, Action<bool> changed, Toggle parent = null)
        {
            var row = new ToggleRow(this, label, note, on, changed);
            Add(row, parent);
            return row.Setting;
        }

        public Choice AddChoice(string label, string[] options, int selected, Action<int> changed, Toggle parent = null)
        {
            var row = new ChoiceRow(this, label, options, selected, changed);
            Add(row, parent);
            return row.Setting;
        }

        public Note AddNote(string text, Toggle parent = null)
        {
            var row = new NoteRow(this, text);
            Add(row, parent);
            return row.Setting;
        }

        // Buttons added one after another under the same toggle share a row.
        public void AddButton(string label, Action click, Toggle parent = null, bool danger = false)
        {
            var row = items.Count > 0 ? items[items.Count - 1] as ActionRow : null;
            if (row == null || row.Links || row.Under != parent) Add(row = new ActionRow(this, links: false), parent);
            row.Add(new ActionButton(this, label, click, danger));
            Reflow();
        }

        // Opens in the browser. Links added one after another share a row, like buttons.
        public void AddLink(string label, string url)
        {
            var row = items.Count > 0 ? items[items.Count - 1] as ActionRow : null;
            if (row == null || !row.Links || row.Under != null) Add(row = new ActionRow(this, links: true), null);
            row.Add(new LinkItem(this, label, url));
            Reflow();
        }

        void Add(Item item, Toggle parent)
        {
            item.Under = parent;
            item.Depth = parent == null ? 0 : parent.Row.Depth + 1;
            item.TabIndex = items.Count;
            items.Add(item);
            page.Controls.Add(item);
            ShowHide();
        }

        internal bool IsFirst(Item item) => items.Count > 0 && items[0] == item;

        static bool InView(Item it) => it.Wanted && (it.Under == null || (it.Under.On && InView(it.Under.Row)));

        // Called whenever a toggle flips or a note empties or fills: rows whose chain of parent
        // toggles changed open or close, and the page reflows around them.
        internal void ShowHide()
        {
            bool animate = CanAnimate;
            double now = Now;
            foreach (var it in items)
            {
                bool want = InView(it);
                if (want == it.Shown) continue;
                it.Open.Set(want ? 1 : 0, now, animate);
                it.Reach(want);
            }
            if (animate) frames.Start();
            Reflow();
        }

        // ---- layout ----

        internal float S => dpi / 96f;
        internal int Px(float logical) => (int)Math.Round(logical * dpi / 96f, MidpointRounding.AwayFromZero);

        internal static int TextHeight(string s, Font f, int width) =>
            TextRenderer.MeasureText(s.Length == 0 ? " " : s, f, new Size(Math.Max(1, width), int.MaxValue), Wrap).Height;

        internal static int TextWidth(string s, Font f) =>
            TextRenderer.MeasureText(s, f, new Size(int.MaxValue, int.MaxValue), OneLine).Width;

        void MakeFonts()
        {
            Body?.Dispose(); Small?.Dispose(); Head?.Dispose(); Underlined?.Dispose();
            Body = Theme.Font(13 * S);
            Small = Theme.Font(12 * S);
            Head = Theme.Semibold(12 * S);
            Underlined = Theme.Font(13 * S, FontStyle.Underline);
        }

        void Rescale()
        {
            MakeFonts();
            foreach (var it in items) it.Remeasure();
            Reflow();
        }

        // Stacks the rows. A row that is opening or closing takes its height times how far open
        // it is, so the rows under it slide instead of jumping.
        void Reflow(int width = 0)
        {
            if (view == null) return;   // the viewport's first resize comes from the constructor
            if (width <= 0) width = view.ClientSize.Width > 0 ? view.ClientSize.Width : Px(PageWidth);
            double now = Now;
            int y = Px(8);
            foreach (var it in items)
            {
                double open = it.Open.At(now);
                if (open != it.LastOpen) { it.LastOpen = open; it.Invalidate(true); }
                if (open <= 0.001)
                {
                    if (it.Placed) { it.Placed = false; it.Visible = false; }
                    continue;
                }
                int x = Px(Edge + Step * it.Depth);
                int w = Math.Max(Px(120), width - x - Px(Edge));
                int full = it.FullHeight(w);
                int h = open >= 0.999 ? full : (int)Math.Round(full * open);
                it.SetBounds(x, y, w, h);
                if (!it.Placed) { it.Placed = true; it.Visible = true; }
                y += h;
            }
            contentHeight = y + Px(16);

            int viewH = view.ClientSize.Height;
            if (scroll.Target > MaxOffset) scroll.Set(MaxOffset, now, false);
            page.SetBounds(0, -Offset, width, Math.Max(contentHeight, viewH));
            strip.SetBounds(view.ClientSize.Width - Px(StripW), 0, Px(StripW), viewH);
            strip.Visible = viewH > 0 && contentHeight > viewH;
            page.Invalidate();
            strip.Invalidate();
        }

        internal void Relayout() => Reflow();

        // Tree lines: one thin rule per nesting level, down the left of each run of rows that
        // hang under the same toggle.
        void PaintGuides(Graphics g)
        {
            int deepest = 0;
            foreach (var it in items) deepest = Math.Max(deepest, it.Depth);
            using (var b = new SolidBrush(Theme.PaperEdge))
                for (int level = 1; level <= deepest; level++)
                {
                    int x = Px(Edge + Step * (level - 1) + Pad + 4), inset = Px(4), w = Math.Max(1, Px(1));
                    Toggle owner = null;
                    int top = 0, bottom = 0;
                    void Rule() { if (owner != null && bottom - top > 2 * inset) g.FillRectangle(b, x, top + inset, w, bottom - top - 2 * inset); }
                    foreach (var it in items)
                    {
                        if (!it.Placed || it.Height <= 0) continue;
                        var at = it.Depth >= level ? AncestorAt(it, level - 1) : null;
                        if (at != owner) { Rule(); owner = at; top = it.Top; }
                        bottom = it.Bottom;
                    }
                    Rule();
                }
        }

        static Toggle AncestorAt(Item it, int depth)
        {
            var t = it.Under;
            while (t != null && t.Row.Depth > depth) t = t.Row.Under;
            return t != null && t.Row.Depth == depth ? t : null;
        }

        // ---- scrolling ----

        int MaxOffset => Math.Max(0, contentHeight - view.ClientSize.Height);
        int Offset => (int)Math.Round(Math.Max(0, Math.Min(MaxOffset, scroll.At(Now))));

        internal void ScrollTo(double y, bool animate)
        {
            animate &= CanAnimate;
            scroll.Set(Math.Max(0, Math.Min(MaxOffset, y)), Now, animate);
            if (animate) frames.Start();
            PlacePage();
        }

        internal void ScrollBy(double dy) => ScrollTo(scroll.Target + dy, true);

        void PlacePage()
        {
            int top = -Offset;
            if (page.Top != top) page.Top = top;
            strip.Invalidate();
        }

        // Keyboard focus brings its row into view. Focus from a click does not, so the row
        // never moves out from under the pointer between press and release.
        internal void Reveal(Part p)
        {
            if (quietFocus) return;
            int top = p.Top, bottom = p.Bottom;
            if (!(p is Item) && p.Parent != null) { top += p.Parent.Top; bottom += p.Parent.Top; }
            int margin = Px(8), viewH = view.ClientSize.Height;
            if (top - margin < scroll.Target) ScrollTo(top - margin, true);
            else if (bottom + margin > scroll.Target + viewH) ScrollTo(bottom + margin - viewH, true);
        }

        internal void FocusQuietly(Control c)
        {
            quietFocus = true;
            try { c.Focus(); }
            finally { quietFocus = false; }
        }

        // ---- animation ----

        internal double Now => clock.Elapsed.TotalMilliseconds;

        // Animations run only where someone can see them: not while hidden or minimized, and
        // not with Windows animations turned off.
        internal bool CanAnimate => IsHandleCreated && Visible && WindowState != FormWindowState.Minimized && !Native.ReducedMotion();

        internal void Kick(Part p)
        {
            if (!CanAnimate) return;
            moving.Add(p);
            frames.Start();
        }

        // One frame: reflow while rows open or close, move the page while it scrolls, and repaint
        // controls with a running animation. Each runs once more after it ends, to paint the end.
        void Frame()
        {
            double now = Now;
            bool layout = false;
            foreach (var it in items) if (it.Open.Moving(now)) { layout = true; break; }
            if (layout || settleLayout) { Reflow(); settleLayout = layout; }
            bool scrolling = scroll.Moving(now);
            if (scrolling || settleScroll) { PlacePage(); settleScroll = scrolling; }
            foreach (var p in moving) p.Invalidate(true);   // a choice's segments paint its sliding selection
            moving.RemoveWhere(p => !p.Moving(now));
            if (!layout && !settleLayout && !scrolling && !settleScroll && moving.Count == 0) frames.Stop();
        }

        static double Linear(double p) => p;

        internal static Color Mix(Color a, Color b, float t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb((int)Math.Round(a.R + (b.R - a.R) * t), (int)Math.Round(a.G + (b.G - a.G) * t), (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        // ---- window ----

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int actual = WindowDpi(Handle, dpi);
            if (actual != dpi) { dpi = actual; Rescale(); }
            // Windows 11 draws the title bar in the page's colors; earlier versions return an
            // error and keep the system title bar, which is fine.
            int caption = ColorRef(Theme.Paper), text = ColorRef(Theme.Ink), border = ColorRef(Theme.PaperEdge);
            Native.DwmSetWindowAttribute(Handle, DWMWA_CAPTION_COLOR, ref caption, 4);
            Native.DwmSetWindowAttribute(Handle, DWMWA_TEXT_COLOR, ref text, 4);
            Native.DwmSetWindowAttribute(Handle, DWMWA_BORDER_COLOR, ref border, 4);
        }

        // Tall enough for every row, up to most of the screen; the page scrolls past that.
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            var area = Screen.FromHandle(Handle).WorkingArea;
            int frameW = Width - ClientSize.Width, frameH = Height - ClientSize.Height;
            int w = Px(PageWidth);
            Reflow(w);
            int h = Math.Min(contentHeight, (int)(area.Height * 0.85) - frameH);
            SetBounds(area.X + (area.Width - w - frameW) / 2, area.Y + (area.Height - h - frameH) / 2, w + frameW, h + frameH);
        }

        protected override bool ProcessDialogKey(Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Escape:
                    Close();
                    return true;
                case Keys.Up:
                case Keys.Left:
                    SelectNextControl(ActiveControl, false, true, true, false);
                    return true;
                case Keys.Down:
                case Keys.Right:
                    SelectNextControl(ActiveControl, true, true, true, false);
                    return true;
                case Keys.PageUp:
                    ScrollBy(-view.ClientSize.Height * 0.9);
                    return true;
                case Keys.PageDown:
                    ScrollBy(view.ClientSize.Height * 0.9);
                    return true;
            }
            return base.ProcessDialogKey(keyData);
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_DPICHANGED:
                    // The page lays itself out from logical units at the new DPI. WinForms' own
                    // rescaling would scale every row a second time, so it is skipped.
                    dpi = (int)(m.WParam.ToInt64() & 0xFFFF);
                    Rescale();
                    var r = (Native.RECT)Marshal.PtrToStructure(m.LParam, typeof(Native.RECT));
                    SetBounds(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
                    m.Result = IntPtr.Zero;
                    return;
                case WM_NCHITTEST:
                    // The width is fixed, so the side edges do nothing and the corners resize
                    // only vertically.
                    base.WndProc(ref m);
                    int hit = m.Result.ToInt32();
                    if (hit == HTLEFT || hit == HTRIGHT) m.Result = new IntPtr(HTBORDER);
                    else if (hit == HTTOPLEFT || hit == HTTOPRIGHT) m.Result = new IntPtr(HTTOP);
                    else if (hit == HTBOTTOMLEFT || hit == HTBOTTOMRIGHT) m.Result = new IntPtr(HTBOTTOM);
                    return;
                case WM_GETMINMAXINFO:
                    base.WndProc(ref m);
                    const int MinTrackY = 28;   // MINMAXINFO.ptMinTrackSize.y
                    Marshal.WriteInt32(m.LParam, MinTrackY, Math.Max(Marshal.ReadInt32(m.LParam, MinTrackY), Px(240)));
                    return;
            }
            base.WndProc(ref m);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            frames.Stop();
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing) return;
            frames.Dispose();
            Body?.Dispose(); Small?.Dispose(); Head?.Dispose(); Underlined?.Dispose();
            appIcon?.Dispose();
        }

        static int ColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

        static int MonitorDpi(Point at)
        {
            try
            {
                var mon = Native.MonitorFromPoint(new Native.POINT { X = at.X, Y = at.Y }, Native.MONITOR_DEFAULTTONEAREST);
                return Native.GetDpiForMonitor(mon, 0, out uint x, out _) == 0 && x > 0 ? (int)x : 96;
            }
            catch (DllNotFoundException) { return 96; }   // Windows 7 and 8.0 have no shcore
        }

        static int WindowDpi(IntPtr hwnd, int fallback)
        {
            try { uint d = GetDpiForWindow(hwnd); return d > 0 ? (int)d : fallback; }
            catch (EntryPointNotFoundException) { return fallback; }   // before Windows 10 1607
        }

        // ---- previews and tests ----

        // The whole page at full height, scrolled-off rows included, at the given scale.
        internal Bitmap Snapshot(float scale)
        {
            Realize(this);   // DrawToBitmap prints only rows that have a window
            dpi = (int)Math.Round(96 * scale);
            Rescale();
            int w = Px(PageWidth);
            Reflow(w);
            page.Height = contentHeight;
            var bmp = new Bitmap(w, contentHeight, PixelFormat.Format24bppRgb);
            page.DrawToBitmap(bmp, new Rectangle(Point.Empty, bmp.Size));
            return bmp;
        }

        static void Realize(Control c)
        {
            if (!c.IsHandleCreated) GC.KeepAlive(c.Handle);
            foreach (Control child in c.Controls) Realize(child);
        }

        internal Part FindPart(string label)
        {
            foreach (var it in items)
            {
                if (it.AccessibleName == label) return it;
                foreach (Control c in it.Controls)
                    if (c is Part p && p.AccessibleName == label) return p;
            }
            return null;
        }

        internal int RowCount => items.Count;
        internal int ContentHeight => contentHeight;

        // ---- controls ----

        // A value that eases from wherever it is toward a target. Painting reads it with At(now),
        // so nothing has to run between frames.
        internal sealed class Glide
        {
            readonly double ms;
            readonly Func<double, double> ease;
            double from, to, start = double.NegativeInfinity;

            internal Glide(double ms, double value, Func<double, double> ease)
            {
                this.ms = ms;
                this.ease = ease;
                from = to = value;
            }

            internal double Target => to;
            internal bool Moving(double now) => now < start + ms;

            internal double At(double now)
            {
                double p = (now - start) / ms;
                return p >= 1 ? to : from + (to - from) * ease(Math.Max(0, p));
            }

            internal void Set(double target, double now, bool animate)
            {
                if (target == to && (animate || !Moving(now))) return;
                from = animate ? At(now) : target;
                to = target;
                start = animate ? now : double.NegativeInfinity;
            }
        }

        // Every owner-drawn control on the page: double-buffered painting, hover and press,
        // Space and Enter, the focus ring, and what screen readers see.
        internal abstract class Part : Control
        {
            internal readonly SettingsWindow Win;
            protected readonly Glide hover = new Glide(90, 0, Linear);
            protected bool pressed;
            bool armed;

            protected Part(SettingsWindow win, bool focusable)
            {
                Win = win;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                SetStyle(ControlStyles.Selectable, focusable);
                TabStop = focusable;
                BackColor = Theme.Paper;
            }

            protected int Px(float logical) => Win.Px(logical);
            internal virtual Size Natural() => Size;
            internal virtual int Inset => Px(RingPad);
            internal virtual bool Moving(double now) => hover.Moving(now);
            internal virtual AccessibleStates ExtraState => AccessibleStates.None;
            internal virtual string ActionName => null;
            internal virtual void Act() { }

            // Rows fade while they open and close; a button or link fades with its row.
            protected virtual float Fade => Parent is Item row ? row.FadeNow : 1;
            protected Color Faded(Color c) => Mix(Theme.Paper, c, Fade);
            internal bool Cued => Focused && ShowFocusCues;
            protected bool Ringed => Win.PreviewRings.Contains(this) || Cued;

            internal void SetHover(bool on)
            {
                hover.Set(on ? 1 : 0, Win.Now, Win.CanAnimate);
                Win.Kick(this);
                Invalidate();
            }

            protected override AccessibleObject CreateAccessibilityInstance() => new PartAccessible(this);

            protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); SetHover(true); }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                SetHover(false);
                if (pressed) { pressed = false; Invalidate(); }
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left) return;
                if (GetStyle(ControlStyles.Selectable)) Win.FocusQuietly(this);
                if (ActionName != null) { pressed = true; Invalidate(); }
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (e.Button != MouseButtons.Left || !pressed) return;
                pressed = false;
                Invalidate();
                if (ClientRectangle.Contains(e.Location)) Act();
            }

            // Acts on release, like a Windows button, so holding the key does not repeat it.
            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);
                if ((e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) && e.Modifiers == Keys.None && ActionName != null)
                {
                    armed = true;
                    e.Handled = true;
                }
            }

            protected override void OnKeyUp(KeyEventArgs e)
            {
                base.OnKeyUp(e);
                if ((e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) && armed)
                {
                    armed = false;
                    e.Handled = true;
                    Act();
                }
            }

            protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Win.Reveal(this); Invalidate(); }
            protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); armed = false; Invalidate(); }
            protected override void OnChangeUICues(UICuesEventArgs e) { base.OnChangeUICues(e); Invalidate(); }

            protected static void Smooth(Graphics g)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            }

            protected void Fill(Graphics g, RectangleF r, float radius, Color c)
            {
                using (var b = new SolidBrush(Faded(c)))
                using (var path = Theme.Round(r, radius))
                    g.FillPath(b, path);
            }

            protected void Ring(Graphics g, RectangleF r, float radius)
            {
                float w = Math.Max(2, Px(2));
                using (var pen = new Pen(Faded(Theme.Slate), w))
                using (var path = Theme.Round(RectangleF.Inflate(r, -w / 2, -w / 2), Math.Max(1, radius - w / 2)))
                    g.DrawPath(pen, path);
            }

            protected void Write(Graphics g, string s, Font f, Rectangle r, Color c, TextFormatFlags flags) =>
                TextRenderer.DrawText(g, s, f, r, Faded(c), flags);
        }

        // Role, name, and description come from the control's own Accessible* properties; this
        // adds the checked state and the default action that screen readers offer.
        internal class PartAccessible : Control.ControlAccessibleObject
        {
            readonly Part part;
            internal PartAccessible(Part part) : base(part) { this.part = part; }

            public override AccessibleStates State => base.State | part.ExtraState;
            public override string DefaultAction => part.ActionName ?? base.DefaultAction;

            public override void DoDefaultAction()
            {
                if (part.ActionName != null) part.Act();
                else base.DoDefaultAction();
            }
        }

        // One line of the page. Under is the toggle it hangs from, if any.
        internal abstract class Item : Part
        {
            internal Toggle Under;
            internal int Depth;
            internal readonly Glide Open = new Glide(180, 1, Ease.Exit);
            internal bool Placed = true;
            internal double LastOpen = 1;
            int measuredFor = -1, measured;

            protected Item(SettingsWindow win, bool focusable) : base(win, focusable) { }

            internal virtual bool Wanted => true;
            internal bool Shown => Open.Target > 0;
            internal float FadeNow => (float)Math.Max(0, Math.Min(1, Open.At(Win.Now)));
            protected override float Fade => FadeNow;

            // Rows paint against their full height even while the layout gives them less, so a
            // closing row is cut off at the bottom rather than squeezed.
            internal int FullHeight(int width)
            {
                if (width != measuredFor) { measured = Measure(width); measuredFor = width; }
                return measured;
            }

            protected abstract int Measure(int width);
            internal virtual void Remeasure() => measuredFor = -1;
            internal virtual void Reach(bool on) { if (GetStyle(ControlStyles.Selectable)) TabStop = on; }
        }

        internal sealed class HeaderRow : Item
        {
            readonly string title;
            int textH;

            internal HeaderRow(SettingsWindow win, string title) : base(win, false)
            {
                this.title = title ?? "";
                AccessibleRole = AccessibleRole.StaticText;
                AccessibleName = this.title;
            }

            protected override int Measure(int width)
            {
                textH = TextHeight(title, Win.Head, width - 2 * Px(Pad));
                return (Win.IsFirst(this) ? Px(8) : Px(24)) + textH + Px(4);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                int full = FullHeight(Width);
                Write(e.Graphics, title, Win.Head, new Rectangle(Px(Pad), full - Px(4) - textH, Width - 2 * Px(Pad), textH), Theme.Slate, Wrap);
            }
        }

        internal sealed class NoteRow : Item
        {
            string body, painted;
            int textH;
            internal readonly Note Setting;

            internal NoteRow(SettingsWindow win, string text) : base(win, false)
            {
                body = painted = text ?? "";
                Setting = new Note(this);
                AccessibleRole = AccessibleRole.StaticText;
                AccessibleName = body;
            }

            internal string Body => body;
            internal override bool Wanted => body.Length > 0;

            internal void SetBody(string value)
            {
                value = value ?? "";
                if (value == body) return;
                body = value;
                // An emptied note keeps its last text while it closes.
                if (value.Length > 0) painted = value;
                AccessibleName = value;
                AccessibilityNotifyClients(AccessibleEvents.NameChange, -1);
                Remeasure();
                Invalidate();
                Win.ShowHide();
            }

            protected override int Measure(int width)
            {
                textH = TextHeight(painted, Win.Small, width - 2 * Px(Pad));
                return Px(2) + textH + Px(8);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                FullHeight(Width);
                Write(e.Graphics, painted, Win.Small, new Rectangle(Px(Pad), Px(2), Width - 2 * Px(Pad), textH), Theme.Slate, Wrap);
            }
        }

        internal sealed class ToggleRow : Item
        {
            readonly string label;
            readonly Action<bool> changed;
            readonly Glide knob;
            string note;
            bool on;
            int labelH, noteH;
            internal readonly Toggle Setting;

            internal ToggleRow(SettingsWindow win, string label, string note, bool on, Action<bool> changed) : base(win, true)
            {
                this.label = label ?? "";
                this.note = note ?? "";
                this.on = on;
                this.changed = changed;
                knob = new Glide(120, on ? 1 : 0, Ease.Exit);
                Setting = new Toggle(this);
                AccessibleRole = AccessibleRole.CheckButton;
                AccessibleName = this.label;
                AccessibleDescription = this.note.Length > 0 ? this.note : null;
            }

            internal bool On => on;
            internal string NoteText => note;
            internal override AccessibleStates ExtraState => on ? AccessibleStates.Checked : AccessibleStates.None;
            internal override string ActionName => on ? "Uncheck" : "Check";
            internal override void Act() => Set(!on, true);
            internal override bool Moving(double now) => base.Moving(now) || knob.Moving(now);

            internal void Set(bool value, bool notify)
            {
                if (value == on) return;
                on = value;
                knob.Set(on ? 1 : 0, Win.Now, Win.CanAnimate);
                Win.Kick(this);
                Invalidate();
                AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
                Win.ShowHide();
                if (notify) changed?.Invoke(on);
            }

            internal void SetNote(string value)
            {
                value = value ?? "";
                if (value == note) return;
                note = value;
                AccessibleDescription = value.Length > 0 ? value : null;
                AccessibilityNotifyClients(AccessibleEvents.DescriptionChange, -1);
                Remeasure();
                Invalidate();
                Win.Relayout();
            }

            int TextWidthFor(int width) => Math.Max(Px(48), width - 2 * Px(Pad) - Px(SwitchW) - Px(16));

            protected override int Measure(int width)
            {
                int w = TextWidthFor(width);
                labelH = TextHeight(label, Win.Body, w);
                noteH = note.Length > 0 ? TextHeight(note, Win.Small, w) : 0;
                int block = labelH + (noteH > 0 ? Px(NoteGap) + noteH : 0);
                return Math.Max(Px(RowMin), block + 2 * Px(RowPad));
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                Smooth(g);
                double now = Win.Now;
                int full = FullHeight(Width);
                var box = new RectangleF(0, 0, Width, full);
                float h = (float)hover.At(now);
                if (pressed) Fill(g, box, Px(Radius), Theme.PaperEdge);
                else if (h > 0) Fill(g, box, Px(Radius), Mix(Theme.Paper, Theme.PaperDeep, h));

                int w = TextWidthFor(Width);
                int block = labelH + (noteH > 0 ? Px(NoteGap) + noteH : 0);
                int y = (full - block) / 2;
                Write(g, label, Win.Body, new Rectangle(Px(Pad), y, w, labelH), Theme.Ink, Wrap);
                if (noteH > 0) Write(g, note, Win.Small, new Rectangle(Px(Pad), y + labelH + Px(NoteGap), w, noteH), Theme.Slate, Wrap);

                var track = new RectangleF(Width - Px(Pad) - Px(SwitchW), (full - Px(SwitchH)) / 2, Px(SwitchW), Px(SwitchH));
                float k = (float)knob.At(now);
                Fill(g, track, track.Height / 2, Mix(Theme.SlateMist, Theme.Slate, k));
                float m = Px(3), d = track.Height - 2 * m;
                var dot = new RectangleF(track.X + m + (track.Width - 2 * m - d) * k, track.Y + m, d, d);
                using (var b = new SolidBrush(Faded(Theme.Paper))) g.FillEllipse(b, dot);
                using (var edge = new Pen(Color.FromArgb((int)(40 * Fade), Theme.Shadow), Math.Max(1f, Win.S * 0.75f))) g.DrawEllipse(edge, dot);

                if (Ringed) Ring(g, box, Px(Radius));
            }
        }

        // A label and a segmented control. Each segment is a small window of its own, so screen
        // readers list the options as radio buttons: window-less children of a WinForms control
        // reach MSAA but not UI Automation, which is what Narrator reads.
        internal sealed class ChoiceRow : Item
        {
            readonly string label;
            internal readonly string[] Options;
            readonly Action<int> changed;
            readonly Glide pill;
            readonly Segment[] segments;
            int selected, labelH, segW, arrangedFor = -1;
            internal readonly Choice Setting;

            internal ChoiceRow(SettingsWindow win, string label, string[] options, int selected, Action<int> changed) : base(win, false)
            {
                this.label = label ?? "";
                Options = options ?? new string[0];
                this.selected = Math.Max(0, Math.Min(Options.Length - 1, selected));
                this.changed = changed;
                pill = new Glide(160, this.selected, Ease.Exit);
                Setting = new Choice(this);
                AccessibleRole = AccessibleRole.Grouping;
                AccessibleName = this.label;
                segments = new Segment[Options.Length];
                for (int i = 0; i < Options.Length; i++)
                {
                    segments[i] = new Segment(win, this, i, Options[i] ?? "") { TabIndex = i };
                    Controls.Add(segments[i]);
                }
                Reach(true);
            }

            internal int Selected => selected;
            internal float PillAt => (float)pill.At(Win.Now);
            internal override bool Moving(double now) => base.Moving(now) || pill.Moving(now);

            // Like a radio group: Tab stops only at the chosen segment, and the arrows move
            // the choice within the group.
            internal override void Reach(bool on)
            {
                for (int i = 0; i < segments.Length; i++) segments[i].TabStop = on && i == selected;
            }

            internal void Select(int index, bool notify, bool focus = false)
            {
                if (Options.Length == 0) return;
                index = Math.Max(0, Math.Min(Options.Length - 1, index));
                bool moved = index != selected;
                if (moved)
                {
                    int was = selected;
                    selected = index;
                    pill.Set(index, Win.Now, Win.CanAnimate);
                    Win.Kick(this);
                    Reach(Shown);
                    Invalidate(true);
                    segments[was].Notify();
                    segments[index].Notify();
                }
                if (focus) segments[index].Focus();
                if (moved && notify) changed?.Invoke(index);
            }

            int GroupWidth => Options.Length * segW + Px(4);

            protected override int Measure(int width)
            {
                segW = 0;
                foreach (var o in Options) segW = Math.Max(segW, TextWidth(o ?? "", Win.Small));
                segW += 2 * Px(SegmentPad);
                labelH = TextHeight(label, Win.Body, LabelWidth(width));
                return Math.Max(Px(RowMin), Math.Max(labelH + 2 * Px(RowPad), Px(SegmentH) + Px(12)));
            }

            internal override void Remeasure()
            {
                base.Remeasure();
                arrangedFor = -1;
                Arrange();
            }

            protected override void OnSizeChanged(EventArgs e)
            {
                base.OnSizeChanged(e);
                Arrange();
            }

            void Arrange()
            {
                if (Width <= 0 || arrangedFor == Width) return;
                arrangedFor = Width;
                var gr = Group(FullHeight(Width));
                for (int i = 0; i < segments.Length; i++)
                    segments[i].SetBounds(gr.X + Px(2) + i * segW, gr.Y + Px(2), segW, gr.Height - Px(4));
            }

            int LabelWidth(int width) => Math.Max(Px(48), width - 2 * Px(Pad) - GroupWidth - Px(16));

            Rectangle Group(int full) => new Rectangle(Width - Px(Pad) - GroupWidth, (full - Px(SegmentH)) / 2, GroupWidth, Px(SegmentH));

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                Smooth(g);
                int full = FullHeight(Width);
                Write(g, label, Win.Body, new Rectangle(Px(Pad), (full - labelH) / 2, LabelWidth(Width), labelH), Theme.Ink, Wrap);
                Fill(g, Group(full), Px(Radius), Theme.PaperDeep);
                if (Ringed || Array.Exists(segments, s => s.Cued)) Ring(g, new RectangleF(0, 0, Width, full), Px(Radius));
            }
        }

        // One option of a ChoiceRow. It paints its own slice of the group, including whatever
        // part of the sliding selection is over it.
        internal sealed class Segment : Part
        {
            readonly ChoiceRow row;
            readonly int index;

            internal Segment(SettingsWindow win, ChoiceRow row, int index, string text) : base(win, true)
            {
                this.row = row;
                this.index = index;
                AccessibleRole = AccessibleRole.RadioButton;
                AccessibleName = text;
            }

            internal override AccessibleStates ExtraState => row.Selected == index ? AccessibleStates.Checked : AccessibleStates.None;
            internal override string ActionName => "Select";
            internal override void Act() => row.Select(index, notify: true);
            internal void Notify() => AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);

            protected override bool IsInputKey(Keys k) => k == Keys.Left || k == Keys.Right || k == Keys.Home || k == Keys.End || base.IsInputKey(k);

            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);
                if (e.Modifiers != Keys.None) return;
                int to;
                switch (e.KeyCode)
                {
                    case Keys.Left: to = row.Selected - 1; break;
                    case Keys.Right: to = row.Selected + 1; break;
                    case Keys.Home: to = 0; break;
                    case Keys.End: to = row.Options.Length - 1; break;
                    default: return;
                }
                row.Select(to, notify: true, focus: true);
                e.Handled = true;
            }

            // The focus ring goes around the whole row, which the row draws.
            protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); row.Invalidate(); }
            protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); row.Invalidate(); }
            protected override void OnChangeUICues(UICuesEventArgs e) { base.OnChangeUICues(e); row.Invalidate(); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                Smooth(g);
                var box = new RectangleF(0, 0, Width, Height);
                using (var b = new SolidBrush(Faded(Theme.PaperDeep))) g.FillRectangle(b, box);
                float h = pressed ? 1 : (float)hover.At(Win.Now);
                if (h > 0 && row.Selected != index) Fill(g, box, Px(4), Mix(Theme.PaperDeep, Theme.PaperEdge, h));
                float at = row.PillAt;
                Fill(g, new RectangleF((at - index) * Width, 0, Width, Height), Px(4), Theme.Slate);
                float cover = Math.Max(0, 1 - Math.Abs(at - index));
                Write(g, AccessibleName, Win.Small, new Rectangle(0, 0, Width, Height), Mix(Theme.Slate, Theme.Paper, cover), Centered);
            }
        }

        // A row of buttons or links, laid left to right from the text edge and wrapped if the
        // window gets too narrow.
        internal sealed class ActionRow : Item
        {
            internal readonly bool Links;
            readonly List<Part> parts = new List<Part>();
            int arrangedFor = -1;

            internal ActionRow(SettingsWindow win, bool links) : base(win, false) { Links = links; }

            internal void Add(Part p)
            {
                p.TabIndex = parts.Count;
                parts.Add(p);
                Controls.Add(p);
                Remeasure();
            }

            protected override int Measure(int width) => Flow(width, false);

            internal override void Remeasure()
            {
                base.Remeasure();
                arrangedFor = -1;
                if (Width > 0) Arrange();
            }

            internal override void Reach(bool on) { foreach (var p in parts) p.TabStop = on; }

            void Arrange()
            {
                if (arrangedFor == Width) return;
                arrangedFor = Width;
                Flow(Width, true);
            }

            protected override void OnSizeChanged(EventArgs e)
            {
                base.OnSizeChanged(e);
                Arrange();
            }

            // Each part carries room on its sides for the focus ring, so x tracks visible edges:
            // the first face lines up with the text of the rows above, and gaps run face to face.
            int Flow(int width, bool apply)
            {
                int left = Px(Pad), top = Px(4), gap = Links ? Px(16) : Px(8), gapY = Px(4);
                int x = left, y = top, line = 0;
                foreach (var p in parts)
                {
                    var size = p.Natural();
                    int inset = p.Inset, face = size.Width - 2 * inset;
                    if (x > left && x + face > width - left) { x = left; y += line + gapY; line = 0; }
                    if (apply) p.SetBounds(x - inset, y, size.Width, size.Height);
                    x += face + gap;
                    line = Math.Max(line, size.Height);
                }
                return y + line + top;
            }

            protected override void OnPaint(PaintEventArgs e) { }
        }

        internal sealed class ActionButton : Part
        {
            readonly string label;
            readonly Action click;
            readonly bool danger;

            internal ActionButton(SettingsWindow win, string label, Action click, bool danger) : base(win, true)
            {
                this.label = label ?? "";
                this.click = click;
                this.danger = danger;
                AccessibleRole = AccessibleRole.PushButton;
                AccessibleName = this.label;
            }

            internal override string ActionName => "Press";
            internal override void Act() => click?.Invoke();

            internal override Size Natural() =>
                new Size(TextWidth(label, Win.Body) + 2 * Px(ButtonPad) + 2 * Px(RingPad), Px(ButtonH) + 2 * Px(RingPad));

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                Smooth(g);
                float ring = Px(RingPad);
                var face = new RectangleF(ring, ring, Width - 2 * ring, Height - 2 * ring);
                Fill(g, face, Px(Radius), pressed ? Theme.PaperEdge : Mix(Theme.PaperDeep, Theme.PaperEdge, (float)hover.At(Win.Now)));
                Write(g, label, Win.Body, Rectangle.Round(face), danger ? Theme.Clip : Theme.Ink, Centered);
                if (Ringed) Ring(g, new RectangleF(0, 0, Width, Height), Px(Radius) + ring);
            }
        }

        internal sealed class LinkItem : Part
        {
            readonly string label, url;

            internal LinkItem(SettingsWindow win, string label, string url) : base(win, true)
            {
                this.label = label ?? "";
                this.url = url ?? "";
                Cursor = Cursors.Hand;
                AccessibleRole = AccessibleRole.Link;
                AccessibleName = this.label;
                AccessibleDescription = this.url;
            }

            internal override string ActionName => "Jump";

            internal override void Act()
            {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                catch (Win32Exception) { }   // no browser is registered for web links
            }

            internal override int Inset => Px(LinkPad);
            internal override Size Natural() => new Size(TextWidth(label, Win.Body) + 2 * Inset, Px(LinkH));

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                Smooth(g);
                bool lit = hover.Target > 0 || pressed;
                var text = new Rectangle(Inset, 0, Width - 2 * Inset, Height);
                Write(g, label, lit ? Win.Underlined : Win.Body, text, pressed ? Theme.Ink : Theme.Slate, OneLine);
                if (Ringed) Ring(g, new RectangleF(0, 0, Width, Height), Px(Radius));
            }
        }

        // Holds the page and moves it to scroll. Composited painting draws a frame of moving rows
        // and the page behind them in one pass, so nothing flashes while a group opens or closes.
        sealed class Viewport : Control
        {
            readonly SettingsWindow win;

            internal Viewport(SettingsWindow win)
            {
                this.win = win;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
                SetStyle(ControlStyles.Selectable, false);
                TabStop = false;
                BackColor = Theme.Paper;
            }

            protected override CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.ExStyle |= WS_EX_COMPOSITED;
                    return cp;
                }
            }

            protected override void OnResize(EventArgs e)
            {
                base.OnResize(e);
                win.Reflow();
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                base.OnMouseWheel(e);
                int lines = SystemInformation.MouseWheelScrollLines;
                double step = lines < 0 ? ClientSize.Height * 0.9 : lines * win.Px(16);
                win.ScrollBy(-e.Delta / 120.0 * step);
                if (e is HandledMouseEventArgs h) h.Handled = true;
            }
        }

        sealed class Page : Control
        {
            readonly SettingsWindow win;

            internal Page(SettingsWindow win)
            {
                this.win = win;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
                SetStyle(ControlStyles.Selectable, false);
                TabStop = false;
                BackColor = Theme.Paper;
            }

            protected override void OnPaint(PaintEventArgs e) => win.PaintGuides(e.Graphics);
        }

        // A slim scroll thumb in the page's colors, over the right margin where no row reaches.
        sealed class ScrollStrip : Control
        {
            readonly SettingsWindow win;
            bool lit;
            int dragFrom = -1;
            double offsetFrom;

            internal ScrollStrip(SettingsWindow win)
            {
                this.win = win;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                SetStyle(ControlStyles.Selectable, false);
                TabStop = false;
                BackColor = Theme.Paper;
            }

            int Track => Height - 2 * win.Px(4);

            Rectangle Thumb()
            {
                int max = win.MaxOffset, content = Math.Max(1, win.contentHeight);
                if (max <= 0) return Rectangle.Empty;
                int h = Math.Max(win.Px(32), (int)((long)Track * win.view.ClientSize.Height / content));
                int y = win.Px(4) + (int)Math.Round((Track - h) * (double)win.Offset / max);
                return new Rectangle(0, y, Width, h);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var thumb = Thumb();
                if (thumb.IsEmpty) return;
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float w = win.Px(lit || dragFrom >= 0 ? 6 : 4);
                var r = new RectangleF(Width - win.Px(3) - w, thumb.Y, w, thumb.Height);
                using (var b = new SolidBrush(lit || dragFrom >= 0 ? Theme.SlateSoft : Theme.SlateMist))
                using (var path = Theme.Round(r, w / 2))
                    g.FillPath(b, path);
            }

            protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); lit = true; Invalidate(); }
            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); lit = false; Invalidate(); }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left) return;
                var thumb = Thumb();
                if (thumb.IsEmpty) return;
                if (e.Y >= thumb.Top && e.Y < thumb.Bottom) { dragFrom = e.Y; offsetFrom = win.Offset; Invalidate(); }
                else win.ScrollBy((e.Y < thumb.Top ? -1 : 1) * win.view.ClientSize.Height * 0.9);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (dragFrom < 0) return;
                var thumb = Thumb();
                int room = Math.Max(1, Track - thumb.Height);
                win.ScrollTo(offsetFrom + (e.Y - dragFrom) * (double)win.MaxOffset / room, false);
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                dragFrom = -1;
                Invalidate();
            }
        }
    }
}

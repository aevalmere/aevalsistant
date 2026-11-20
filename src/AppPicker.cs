using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Aevalsistant
{
    // One running app the picker can offer.
    sealed class RunningApp
    {
        public string Name = "";    // process name in lower case: "blender"
        public string Label = "";   // "Blender"
        public bool Windowed;       // has a visible window; the rest only show up when searched for

        // Apps with a window first, then background processes, one entry per process name.
        public static List<RunningApp> List()
        {
            var byName = new Dictionary<string, RunningApp>();
            int self = Process.GetCurrentProcess().Id;
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        if (p.Id == self || p.Id <= 4) continue;
                        string name = p.ProcessName.ToLowerInvariant();
                        if (name.Length == 0) continue;
                        bool windowed = p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle.Length > 0;
                        if (byName.TryGetValue(name, out var known))
                        {
                            if (windowed && !known.Windowed) { known.Windowed = true; known.Label = Describe(p, name); }
                            continue;
                        }
                        byName[name] = new RunningApp { Name = name, Windowed = windowed, Label = windowed ? Describe(p, name) : name };
                    }
                    catch (InvalidOperationException) { }   // exited while being read
                    catch (System.ComponentModel.Win32Exception) { }
                }
            }
            return byName.Values.OrderBy(a => a.Windowed ? 0 : 1).ThenBy(a => a.Label, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // The exe's own description ("Blender"), or its name with a capital letter.
        static string Describe(Process p, string name)
        {
            try
            {
                string d = p.MainModule?.FileVersionInfo.FileDescription?.Trim();
                if (!string.IsNullOrEmpty(d) && d.Length <= 60) return d;
            }
            catch (System.ComponentModel.Win32Exception) { }   // elevated, or a 64-bit process from a 32-bit one
            catch (InvalidOperationException) { }
            return char.ToUpperInvariant(name[0]) + name.Substring(1);
        }

        // Every running process name, for the watched-apps check each sweep.
        public static HashSet<string> Names()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
                using (p)
                {
                    try { names.Add(p.ProcessName); }
                    catch (InvalidOperationException) { }
                }
            return names;
        }
    }

    // "Add an app": a search box over the apps running now. Clicking one adds it to the
    // watched list, or takes it off again if it is already there. Changes save at once.
    sealed class AppPicker : Form
    {
        readonly TextBox search = new TextBox();
        readonly ListBox list = new ListBox();
        readonly Label hint = new Label();
        readonly Func<string, bool> isWatched;
        readonly Action<RunningApp> add;
        readonly Action<string> remove;
        readonly Icon appIcon;
        List<RunningApp> all = new List<RunningApp>();
        Font body, small;

        public AppPicker(Func<string, bool> isWatched, Action<RunningApp> add, Action<string> remove)
        {
            this.isWatched = isWatched;
            this.add = add;
            this.remove = remove;
            Text = "Add an app";
            appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Icon = appIcon;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Paper;
            // Theme fonts are sized in pixels, so the window scales itself like the Settings page.
            AutoScaleMode = AutoScaleMode.None;
            float k = DeviceDpi / 96f;
            int Px(float v) => (int)Math.Round(v * k);
            Font = body = Theme.Font(13 * k);
            small = Theme.Font(12 * k);
            ClientSize = new Size(Px(420), Px(460));
            MinimumSize = new Size(Px(320), Px(300));
            Padding = new Padding(Px(12));

            hint.Text = "Keeps the PC awake while the app you pick is open. Click again to take it off.";
            hint.Dock = DockStyle.Top;
            hint.ForeColor = Theme.Slate;
            hint.Font = small;
            hint.Height = Px(40);

            search.Dock = DockStyle.Top;
            search.BorderStyle = BorderStyle.FixedSingle;
            search.BackColor = Color.White;
            search.ForeColor = Theme.Ink;
            search.TextChanged += (s, e) => Fill();
            search.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Down && list.Items.Count > 0) { list.Focus(); if (list.SelectedIndex < 0) list.SelectedIndex = 0; e.Handled = true; }
                if (e.KeyCode == Keys.Enter && list.Items.Count > 0) { Pick(list.SelectedIndex >= 0 ? list.SelectedIndex : 0); e.Handled = e.SuppressKeyPress = true; }
            };
            SendCueBanner(search, "Search open apps");

            var gap = new Panel { Dock = DockStyle.Top, Height = Px(8), BackColor = Theme.Paper };

            list.Dock = DockStyle.Fill;
            list.BorderStyle = BorderStyle.None;
            list.BackColor = Theme.Paper;
            list.ForeColor = Theme.Ink;
            list.DrawMode = DrawMode.OwnerDrawFixed;
            list.ItemHeight = Px(40);
            list.IntegralHeight = false;
            list.DrawItem += DrawRow;
            list.MouseClick += (s, e) => { int i = list.IndexFromPoint(e.Location); if (i >= 0) Pick(i); };
            list.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) { Pick(list.SelectedIndex); e.Handled = e.SuppressKeyPress = true; } };

            Controls.Add(list);
            Controls.Add(gap);
            Controls.Add(search);
            Controls.Add(hint);
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            Activated += (s, e) => Reload();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            const int DWMWA_BORDER_COLOR = 34, DWMWA_CAPTION_COLOR = 35, DWMWA_TEXT_COLOR = 36;
            int caption = ColorRef(Theme.Paper), text = ColorRef(Theme.Ink), border = ColorRef(Theme.PaperEdge);
            Native.DwmSetWindowAttribute(Handle, DWMWA_CAPTION_COLOR, ref caption, 4);
            Native.DwmSetWindowAttribute(Handle, DWMWA_TEXT_COLOR, ref text, 4);
            Native.DwmSetWindowAttribute(Handle, DWMWA_BORDER_COLOR, ref border, 4);
        }

        static int ColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

        bool loading;

        // Reading every process's description takes a moment, so it runs off the UI thread.
        void Reload()
        {
            if (loading) return;
            loading = true;
            Task.Run(() => RunningApp.List()).ContinueWith(t =>
            {
                if (IsDisposed) return;
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        loading = false;
                        if (t.Status == TaskStatus.RanToCompletion) all = t.Result;
                        Fill();
                    }));
                }
                catch (InvalidOperationException) { }   // closed in the meantime
            });
        }

        // With nothing typed, apps with a window; typing also searches background processes.
        void Fill()
        {
            string q = search.Text.Trim();
            var shown = all.Where(a => q.Length == 0
                    ? a.Windowed
                    : a.Label.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || a.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            string keep = (list.SelectedItem as RunningApp)?.Name;
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var a in shown) list.Items.Add(a);
            int again = shown.FindIndex(a => a.Name == keep);
            if (again >= 0) list.SelectedIndex = again;
            list.EndUpdate();
        }

        void Pick(int index)
        {
            if (index < 0 || index >= list.Items.Count) return;
            var app = (RunningApp)list.Items[index];
            if (isWatched(app.Name)) remove(app.Name); else add(app);
            list.Invalidate();
        }

        void DrawRow(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var app = (RunningApp)list.Items[e.Index];
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using (var bg = new SolidBrush(selected ? Theme.PaperEdge : Theme.Paper)) e.Graphics.FillRectangle(bg, e.Bounds);
            bool added = isWatched(app.Name);
            int pad = (int)Math.Round(8 * DeviceDpi / 96f);
            string right = added ? "Added" : app.Windowed ? "" : "background";
            int rightW = right.Length > 0 ? TextRenderer.MeasureText(right, small).Width + pad : 0;
            var labelBox = new Rectangle(e.Bounds.X + pad, e.Bounds.Y, e.Bounds.Width - 2 * pad - rightW, e.Bounds.Height / 2 + 2);
            var nameBox = new Rectangle(labelBox.X, e.Bounds.Y + e.Bounds.Height / 2, labelBox.Width, e.Bounds.Height / 2 - 2);
            const TextFormatFlags One = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(e.Graphics, app.Label, body, labelBox, Theme.Ink, One | TextFormatFlags.Bottom);
            TextRenderer.DrawText(e.Graphics, app.Name + ".exe", small, nameBox, Theme.Slate, One | TextFormatFlags.Top);
            if (right.Length > 0)
                TextRenderer.DrawText(e.Graphics, right, small, new Rectangle(e.Bounds.Right - rightW - pad, e.Bounds.Y, rightW, e.Bounds.Height),
                    added ? Theme.Ink : Theme.Slate, One | TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        static void SendCueBanner(TextBox box, string text) =>
            box.HandleCreated += (s, e) => SendMessage(box.Handle, 0x1501 /* EM_SETCUEBANNER */, new IntPtr(1), text);

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing) return;
            appIcon?.Dispose();
            body?.Dispose();
            small?.Dispose();
        }
    }
}

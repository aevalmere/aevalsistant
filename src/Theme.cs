using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace Aevalsistant
{
    // Values from DESIGN.md, sampled from the profile image.
    static class Theme
    {
        public static readonly Color Paper = Hex("#FFF6F1");
        public static readonly Color PaperDeep = Hex("#FBEBE3");
        public static readonly Color PaperEdge = Hex("#F0DDD3");
        public static readonly Color Ink = Hex("#3E3438");
        public static readonly Color Slate = Hex("#586C7D");
        public static readonly Color SlateSoft = Hex("#9DB0B9");
        public static readonly Color SlateMist = Hex("#C9D5DA");
        public static readonly Color Blush = Hex("#E9A9AE");
        public static readonly Color Clip = Hex("#8E4A52");
        public static readonly Color Skin = Hex("#FBE3D6");
        public static readonly Color Shadow = Hex("#5A3A3A");

        public static Color Hex(string h) =>
            Color.FromArgb(Convert.ToInt32(h.Substring(1, 2), 16), Convert.ToInt32(h.Substring(3, 2), 16), Convert.ToInt32(h.Substring(5, 2), 16));

        static string family;
        public static string Family
        {
            get
            {
                if (family != null) return family;
                using (var fonts = new InstalledFontCollection())
                {
                    family = "Segoe UI";
                    foreach (var f in fonts.Families)
                        if (f.Name == "Segoe UI Variable Text") { family = f.Name; break; }
                }
                return family;
            }
        }

        static string semibold;
        static string SemiboldFamily
        {
            get
            {
                if (semibold != null) return semibold;
                semibold = "";
                using (var fonts = new InstalledFontCollection())
                    foreach (var want in new[] { "Segoe UI Variable Text Semibold", "Segoe UI Semibold" })
                        foreach (var f in fonts.Families)
                            if (semibold.Length == 0 && f.Name == want) semibold = f.Name;
                return semibold;
            }
        }

        public static Font Font(float px, FontStyle style = FontStyle.Regular) =>
            new Font(Family, px, style, GraphicsUnit.Pixel);

        // GDI+ only knows regular and bold; the semibold cut is its own family name on Windows.
        public static Font Semibold(float px) =>
            SemiboldFamily.Length > 0 ? new Font(SemiboldFamily, px, FontStyle.Regular, GraphicsUnit.Pixel) : Font(px, FontStyle.Bold);

        public static Bitmap Resource(string name)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
                return s == null ? null : new Bitmap(s);
        }


        public static GraphicsPath Round(RectangleF r, float radius)
        {
            float d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // The tray glyph: the sleepy face from the profile picture, reduced to shapes that
        // still read at 16 px. Eyes closed when idle; open with blush while keeping awake.
        public static Bitmap TrayGlyph(int size, bool awake)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float s = size / 32f;
                // face
                using (var skin = new SolidBrush(Skin))
                using (var ink = new Pen(Ink, 1.6f * s))
                {
                    var face = new RectangleF(3 * s, 5 * s, 26 * s, 24 * s);
                    g.FillEllipse(skin, face);
                    g.DrawEllipse(ink, face);
                }
                // hair cap with a few tufts
                using (var hair = new SolidBrush(awake ? SlateSoft : Hex("#8FA3AE")))
                using (var ink = new Pen(Ink, 1.6f * s) { LineJoin = LineJoin.Round })
                using (var path = new GraphicsPath())
                {
                    path.AddBezier(2.2f * s, 20 * s, 1 * s, 9 * s, 9 * s, 2.5f * s, 16 * s, 3 * s);
                    path.AddBezier(16 * s, 3 * s, 24 * s, 2.5f * s, 31 * s, 9 * s, 29.8f * s, 20 * s);
                    path.AddLine(29.8f * s, 20 * s, 26 * s, 14 * s);
                    path.AddLine(26 * s, 14 * s, 22 * s, 17 * s);
                    path.AddLine(22 * s, 17 * s, 19 * s, 12.5f * s);
                    path.AddLine(19 * s, 12.5f * s, 15 * s, 16.5f * s);
                    path.AddLine(15 * s, 16.5f * s, 11 * s, 12.5f * s);
                    path.AddLine(11 * s, 12.5f * s, 7.5f * s, 16.5f * s);
                    path.CloseFigure();
                    g.FillPath(hair, path);
                    g.DrawPath(ink, path);
                }
                // hair clip
                using (var clip = new SolidBrush(Clip))
                    g.FillEllipse(clip, 16.5f * s, 5.5f * s, 6 * s, 3.2f * s);
                // eyes
                using (var ink = new Pen(Ink, 1.7f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                using (var dot = new SolidBrush(Ink))
                {
                    if (awake)
                    {
                        g.FillEllipse(dot, 10.2f * s, 19.2f * s, 3.4f * s, 3.8f * s);
                        g.FillEllipse(dot, 18.4f * s, 19.2f * s, 3.4f * s, 3.8f * s);
                    }
                    else
                    {
                        g.DrawArc(ink, 9.5f * s, 18 * s, 5 * s, 4 * s, 20, 140);
                        g.DrawArc(ink, 17.5f * s, 18 * s, 5 * s, 4 * s, 20, 140);
                    }
                }
                if (awake)
                    using (var blush = new SolidBrush(Color.FromArgb(200, Blush)))
                    {
                        g.FillEllipse(blush, 6 * s, 23 * s, 5 * s, 3 * s);
                        g.FillEllipse(blush, 21 * s, 23 * s, 5 * s, 3 * s);
                    }
            }
            return bmp;
        }

        public static Bitmap Dot(int size, Color fill, bool hollow)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float d = size * 0.5f, o = (size - d) / 2;
                if (hollow) using (var p = new Pen(fill, Math.Max(1f, size / 10f))) g.DrawEllipse(p, o, o, d, d);
                else using (var b = new SolidBrush(fill)) g.FillEllipse(b, o, o, d, d);
            }
            return bmp;
        }
    }

    sealed class PaperMenuRenderer : ToolStripProfessionalRenderer
    {
        public PaperMenuRenderer() : base(new PaperColors()) { RoundedEdges = false; }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled || e.Item.Tag is string) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(4, 1, e.Item.Width - 8, e.Item.Height - 2);
            using (var path = Theme.Round(r, 5))
            using (var b = new SolidBrush(Theme.PaperDeep))
                g.FillPath(b, path);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            string tag = e.Item.Tag as string;
            e.TextColor = !e.Item.Enabled ? Theme.SlateSoft : tag == "status" ? Theme.Slate : Theme.Ink;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = e.ImageRectangle;
            using (var p = new Pen(Theme.Clip, Math.Max(1.6f, r.Height / 9f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                float x = r.X + r.Width * 0.18f, y = r.Y + r.Height * 0.52f;
                g.DrawLines(p, new[]
                {
                    new PointF(x, y),
                    new PointF(r.X + r.Width * 0.42f, r.Y + r.Height * 0.74f),
                    new PointF(r.X + r.Width * 0.84f, r.Y + r.Height * 0.28f),
                });
            }
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (var p = new Pen(Theme.PaperEdge))
                e.Graphics.DrawLine(p, 12, y, e.Item.Width - 12, y);
        }
    }

    sealed class PaperColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Paper;
        public override Color ImageMarginGradientBegin => Theme.Paper;
        public override Color ImageMarginGradientMiddle => Theme.Paper;
        public override Color ImageMarginGradientEnd => Theme.Paper;
        public override Color MenuBorder => Theme.PaperEdge;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => Theme.PaperDeep;
        public override Color CheckBackground => Color.Transparent;
        public override Color CheckSelectedBackground => Color.Transparent;
        public override Color CheckPressedBackground => Color.Transparent;
        public override Color SeparatorDark => Theme.PaperEdge;
        public override Color SeparatorLight => Theme.Paper;
    }
}

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using log4net;
using MissionPlanner.Controls;

namespace MissionPlanner.Utilities
{
    /// <summary>
    /// Sarus Glass, an opt-in theme (SarusGlass.mpsystheme). On top of its colours it adds restrained effects:
    /// a frosted menu bar (a slight tonal step, fine grain, hairline edges), a slate window frame instead of the
    /// Windows accent colour, group boxes drawn as cards with a rounded hairline and a light top edge, and softly
    /// raised buttons that sink when pressed (MyButton.SoftRaised).
    /// The HUD, the map and flight warnings keep their own colours in every theme.
    /// </summary>
    public static class SarusGlass
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const string ThemeFile = "SarusGlass.mpsystheme";

        public static bool Enabled { get; private set; }

        private static Image menuBackground;
        private static Form framedForm;

        /// <summary>called by ThemeColorTable.SetTheme with the theme being applied</summary>
        public static void Apply(string themeName)
        {
            Enabled = string.Equals(themeName, ThemeFile, StringComparison.OrdinalIgnoreCase);
            MyButton.SoftRaised = Enabled;
            ReleaseMenuBackground();
            if (Enabled && MainV2.instance != null)
            {
                MainV2.instance.switchicons(new MainV2.sarusglassmenuicons());
                // switchicons does nothing when Glass is applied again, so hand the menu its fresh surface here
                MainV2.instance.MainMenu.BackgroundImage = MenuBackground();
                ApplyFrame(MainV2.instance);
            }
            else if (framedForm != null)
            {
                ResetFrame(framedForm);
            }
        }

        /// <summary>drop the cached menu surface once nothing on the menu bar still draws it</summary>
        private static void ReleaseMenuBackground()
        {
            var old = menuBackground;
            menuBackground = null;
            if (old == null)
                return;
            var menu = MainV2.instance?.MainMenu;
            if (menu != null)
            {
                if (ReferenceEquals(menu.BackgroundImage, old))
                    menu.BackgroundImage = null;
                foreach (ToolStripItem item in menu.Items)
                    if (ReferenceEquals(item.BackgroundImage, old))
                        item.BackgroundImage = null;
            }
            old.Dispose();
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_TEXT_COLOR = 36;
        private const int DWMWA_COLOR_DEFAULT = unchecked((int) 0xFFFFFFFF);

        private static int ColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

        /// <summary>
        /// Slate title bar and border in the theme's own colours, whatever accent colour Windows uses (Windows 11;
        /// older Windows ignores it)
        /// </summary>
        public static void ApplyFrame(Form form)
        {
            if (!Enabled || form == null)
                return;
            void Set()
            {
                if (!Enabled)
                    return;
                try
                {
                    int on = 1;
                    int caption = ColorRef(ThemeManager.ControlBGColor);
                    int text = ColorRef(ThemeManager.TextColor);
                    int border = ColorRef(Shade(ThemeManager.ControlBGColor, 0.12));
                    DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
                    DwmSetWindowAttribute(form.Handle, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
                    DwmSetWindowAttribute(form.Handle, DWMWA_TEXT_COLOR, ref text, sizeof(int));
                    DwmSetWindowAttribute(form.Handle, DWMWA_BORDER_COLOR, ref border, sizeof(int));
                    framedForm = form;
                }
                catch (Exception ex)
                {
                    log.Info("window frame effect not available: " + ex.Message);
                }
            }
            if (form.IsHandleCreated)
                Set();
            else
                form.HandleCreated += (s, e) => Set();
        }

        /// <summary>
        /// Give the window back the caption, text and border colours Windows would choose, after Glass was applied
        /// </summary>
        private static void ResetFrame(Form form)
        {
            framedForm = null;
            if (form == null || form.IsDisposed || !form.IsHandleCreated)
                return;
            try
            {
                int off = 0;
                int auto = DWMWA_COLOR_DEFAULT;
                DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref off, sizeof(int));
                DwmSetWindowAttribute(form.Handle, DWMWA_CAPTION_COLOR, ref auto, sizeof(int));
                DwmSetWindowAttribute(form.Handle, DWMWA_TEXT_COLOR, ref auto, sizeof(int));
                DwmSetWindowAttribute(form.Handle, DWMWA_BORDER_COLOR, ref auto, sizeof(int));
            }
            catch (Exception ex)
            {
                log.Info("window frame reset not available: " + ex.Message);
            }
        }

        /// <summary>
        /// The frosted surface tiled behind the menu bar: the panel colour, a few percent lighter at the top, with a
        /// fixed fine grain and a hairline at the top and bottom edges.
        /// </summary>
        public static Image MenuBackground()
        {
            if (menuBackground != null)
                return menuBackground;

            var baseColor = ThemeManager.ControlBGColor;
            const int w = 96, h = 256;
            var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            var rnd = new Random(7); // the same grain every time
            for (int y = 0; y < h; y++)
            {
                // +6% light at the top easing to the base colour by 40% of the height, -3% at the very bottom
                double t = y / (double) (h - 1);
                double lift = t < 0.4 ? 0.06 * (1 - t / 0.4) : -0.03 * ((t - 0.4) / 0.6);
                for (int x = 0; x < w; x++)
                {
                    int grain = rnd.Next(-3, 4);
                    bmp.SetPixel(x, y, Color.FromArgb(Tone(baseColor.R, lift, grain), Tone(baseColor.G, lift, grain),
                        Tone(baseColor.B, lift, grain)));
                }
            }
            using (var g = Graphics.FromImage(bmp))
            {
                using (var top = new Pen(Color.FromArgb(40, 255, 255, 255)))
                    g.DrawLine(top, 0, 0, w, 0);
            }
            menuBackground = bmp;
            return bmp;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GroupBox, object> styled =
            new System.Runtime.CompilerServices.ConditionalWeakTable<GroupBox, object>();

        /// <summary>
        /// Draw a group box as a card: rounded hairline edge, a light line along the top, the caption set into the
        /// edge. The inside keeps the page colour, so labels and inputs sit on it exactly as before.
        /// </summary>
        public static void StyleGroupBox(GroupBox box)
        {
            if (!Enabled || box == null || styled.TryGetValue(box, out _))
                return;
            styled.Add(box, null);
            box.EnabledChanged += (s, e) => box.Invalidate();
            box.Paint += (s, e) =>
            {
                if (!Enabled)
                    return;
                var g = e.Graphics;
                var back = box.BackColor;
                g.Clear(back);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                // the caption is measured and drawn with the same API (GDI, TextRenderer), so the gap fits the text exactly
                var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.Left | TextFormatFlags.Top;
                var size = TextRenderer.MeasureText(g, string.IsNullOrEmpty(box.Text) ? " " : box.Text, box.Font,
                    new Size(int.MaxValue, int.MaxValue), flags);
                float top = size.Height / 2f;
                var card = new RectangleF(0.5f, top, box.Width - 1.5f, box.Height - top - 1.5f);
                // a disabled box draws its edge and caption fainter
                var edgeColor = Shade(back, box.Enabled ? 0.16 : 0.08);
                var textColor = box.Enabled ? box.ForeColor : Blend(box.ForeColor, back, 0.55);
                using (var path = Round(card, 5f))
                using (var edge = new Pen(edgeColor))
                    g.DrawPath(edge, path);
                using (var light = new Pen(Color.FromArgb(60, 255, 255, 255)))
                    g.DrawLine(light, card.X + 6, card.Y + 1, card.Right - 6, card.Y + 1);
                if (!string.IsNullOrEmpty(box.Text))
                {
                    var capRect = new RectangleF(9, 0, size.Width + 2, size.Height);
                    using (var gap = new SolidBrush(back))
                        g.FillRectangle(gap, capRect);
                    TextRenderer.DrawText(g, box.Text, box.Font, new Point(10, 0), textColor, flags);
                }
            };
            box.Invalidate();
        }

        private static System.Drawing.Drawing2D.GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new System.Drawing.Drawing2D.GraphicsPath();
            float d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private static Color Blend(Color from, Color to, double t)
        {
            return Color.FromArgb((int) Math.Round(from.R + (to.R - from.R) * t),
                (int) Math.Round(from.G + (to.G - from.G) * t), (int) Math.Round(from.B + (to.B - from.B) * t));
        }

        private static Color Shade(Color c, double f)
        {
            int Mix(int v) => Math.Max(0, Math.Min(255, (int) Math.Round(f >= 0 ? v + (255 - v) * f : v * (1 + f))));
            return Color.FromArgb(Mix(c.R), Mix(c.G), Mix(c.B));
        }

        private static int Tone(int v, double lift, int grain)
        {
            double r = lift >= 0 ? v + (255 - v) * lift : v * (1 + lift);
            return Math.Max(0, Math.Min(255, (int) Math.Round(r) + grain));
        }
    }
}

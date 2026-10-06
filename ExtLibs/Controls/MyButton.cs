using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;


using System.Drawing.Drawing2D;

namespace MissionPlanner.Controls
{
    public class MyButton : Button
    {
        bool _mouseover = false;
        bool _mousedown = false;

        internal Color _BGGradTop;
        internal Color _BGGradBot;
        internal Color _TextColor;
        internal Color _TextColorNotEnabled;
        internal Color _Outline;
        internal Color _ColorNotEnabled;
        internal Color _ColorMouseOver;
        internal Color _ColorMouseDown;

        bool inOnPaint = false;

        /// <summary>
        /// Sarus Glass theme: buttons are drawn as one soft raised surface (a light top edge, one short shadow,
        /// small corners) that sinks when pressed. Off for every other theme.
        /// </summary>
        public static bool SoftRaised;

        [System.ComponentModel.Browsable(true), System.ComponentModel.Category("Colors")]
        [DefaultValue(typeof(Color), "0x94, 0xc1, 0x1f")]
        public Color BGGradTop { get { return _BGGradTop; } set { _BGGradTop = value; this.Invalidate(); } }
        [System.ComponentModel.Browsable(true), System.ComponentModel.Category("Colors")]
        [DefaultValue(typeof(Color), "0xcd, 0xe2, 0x96")]
        public Color BGGradBot { get { return _BGGradBot; } set { _BGGradBot = value; this.Invalidate(); } }
        [System.ComponentModel.Browsable(true), System.ComponentModel.Category("Colors")]
        [DefaultValue(typeof(Color), "73, 0x2b, 0x3a, 0x03")]
        public Color ColorNotEnabled { get { return _ColorNotEnabled; } set { _ColorNotEnabled = value; this.Invalidate(); } }
        [System.ComponentModel.Browsable(true), System.ComponentModel.Category("Colors")]
        [DefaultValue(typeof(Color), "73, 0x2b, 0x3a, 0x03")]
        public Color ColorMouseOver { get { return _ColorMouseOver; } set { _ColorMouseOver = value; this.Invalidate(); } }
        [System.ComponentModel.Browsable(true), System.ComponentModel.Category("Colors")]
        [DefaultValue(typeof(Color), "150, 0x2b, 0x3a, 0x03")]
        public Color ColorMouseDown { get { return _ColorMouseDown; } set { _ColorMouseDown = value; this.Invalidate(); } }

        // i want to ignore forecolor
        [System.ComponentModel.Browsable(true), System.ComponentModel.Category("Colors")]
        [DefaultValue(typeof(Color), "0x40, 0x57, 0x04")]
        public Color TextColor { get { return _TextColor; } set { _TextColor = value; this.Invalidate(); } }
        [System.ComponentModel.Browsable(true), System.ComponentModel.Category("Colors")]
        public Color TextColorNotEnabled { get { return (_TextColorNotEnabled.IsEmpty) ? _TextColor : _TextColorNotEnabled; } set { _TextColorNotEnabled = value; this.Invalidate(); } }
        [System.ComponentModel.Browsable(true), System.ComponentModel.Category("Colors")]
        [DefaultValue(typeof(Color), "0x79, 0x94, 0x29")]
        public Color Outline { get { return _Outline; } set { _Outline = value; this.Invalidate(); } }

        protected override Size DefaultSize => base.DefaultSize;

        public MyButton()
        {
            _BGGradTop = Color.FromArgb(0x94, 0xc1, 0x1f);
            _BGGradBot = Color.FromArgb(0xcd, 0xe2, 0x96);
            _TextColor = Color.FromArgb(0x40, 0x57, 0x04);
            _Outline = Color.FromArgb(0x79, 0x94, 0x29);
            _ColorNotEnabled = Color.FromArgb(73, 0x2b, 0x3a, 0x03);
            _ColorMouseOver = Color.FromArgb(73, 0x2b, 0x3a, 0x03);
            _ColorMouseDown = Color.FromArgb(150, 0x2b, 0x3a, 0x03);
        }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            //base.OnPaint(pevent);

            if (inOnPaint)
                return;

            inOnPaint = true;

            if (SoftRaised)
            {
                try
                {
                    PaintSoftRaised(pevent.Graphics);
                }
                catch { }
                inOnPaint = false;
                return;
            }

            try
            {
                Graphics gr = pevent.Graphics;

                gr.Clear(this.BackColor);

                gr.SmoothingMode = SmoothingMode.AntiAlias;

                Rectangle outside = new Rectangle(0, 0, this.Width, this.Height);

                LinearGradientBrush linear = new LinearGradientBrush(outside, BGGradTop, BGGradBot, LinearGradientMode.Vertical);

                Pen mypen = new Pen(Outline, 1);

                GraphicsPath outline = new GraphicsPath();

                float wid = this.Height / 3f;

                wid = 1;

                int width = this.Width - 1;
                int height = this.Height - 1;

                // tl
                outline.AddArc(0, 0, wid, wid, 180, 90);
                // top line
                outline.AddLine(wid, 0, width - wid, 0);
                // tr
                outline.AddArc(width - wid, 0, wid, wid, 270, 90);
                // br
                outline.AddArc(width - wid, height - wid, wid, wid, 0, 90);
                // bottom line
                outline.AddLine(wid, height, width - wid, height);
                // bl
                outline.AddArc(0, height - wid, wid, wid, 90, 90);
                // left line
                outline.AddLine(0, height - wid, 0, wid - wid / 2);


                gr.FillPath(linear, outline);

                gr.DrawPath(mypen, outline);

                SolidBrush mybrush = this.Enabled ? new SolidBrush(TextColor) : new SolidBrush(TextColorNotEnabled);

                if (_mouseover)
                {
                    SolidBrush brush = new SolidBrush(ColorMouseOver);

                    gr.FillPath(brush, outline);
                }
                if (_mousedown)
                {
                    SolidBrush brush = new SolidBrush(ColorMouseDown);

                    gr.FillPath(brush, outline);
                }

                if (!this.Enabled)
                {
                    SolidBrush brush = new SolidBrush(_ColorNotEnabled);

                    gr.FillPath(brush, outline);
                }


                StringFormat stringFormat = new StringFormat();
                stringFormat.Alignment = StringAlignment.Center;
                stringFormat.LineAlignment = StringAlignment.Center;

                string display = this.Text;
                int amppos = display.IndexOf('&');
                if (amppos != -1)
                    display = display.Remove(amppos, 1);

                gr.DrawString(display, this.Font, mybrush, outside, stringFormat);
            }
            catch { }

            inOnPaint = false;
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
        }

        private static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private static Color Shade(Color c, float f)
        {
            // f > 0 lightens towards white, f < 0 darkens towards black
            int Mix(int v) => (int) Math.Round(f >= 0 ? v + (255 - v) * f : v * (1 + f));
            return Color.FromArgb(c.A, Mix(c.R), Mix(c.G), Mix(c.B));
        }

        private void PaintSoftRaised(Graphics gr)
        {
            var back = Parent?.BackColor ?? BackColor;
            gr.Clear(back.A == 255 ? back : BackColor);
            gr.SmoothingMode = SmoothingMode.AntiAlias;

            bool pressed = _mousedown && Enabled;
            const float radius = 4f;
            var face = new RectangleF(1.5f, 1.5f, Width - 4f, Height - 4.5f);
            if (pressed)
                face.Offset(0, 1f);

            using (var facePath = RoundRect(face, radius))
            {
                if (!pressed && Enabled)
                {
                    // one short, soft shadow below the face
                    using (var shadowPath = RoundRect(new RectangleF(face.X + 0.5f, face.Y + 2f, face.Width, face.Height), radius))
                    using (var shadow = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
                        gr.FillPath(shadow, shadowPath);
                }

                var top = pressed ? Shade(BGGradTop, -0.10f) : Shade(BGGradTop, 0.05f);
                var bottom = pressed ? Shade(BGGradTop, -0.04f) : BGGradTop;
                using (var fill = new LinearGradientBrush(face, top, bottom, LinearGradientMode.Vertical))
                    gr.FillPath(fill, facePath);

                if (_mouseover && Enabled && !pressed)
                    using (var over = new SolidBrush(ColorMouseOver))
                        gr.FillPath(over, facePath);

                // light top edge when raised, dark top edge when pressed in
                using (var edge = new Pen(pressed ? Color.FromArgb(90, 0, 0, 0) : Color.FromArgb(55, 255, 255, 255), 1f))
                    gr.DrawLine(edge, face.X + radius, face.Y + 0.5f, face.Right - radius, face.Y + 0.5f);

                using (var outline = new Pen(Color.FromArgb(150, Outline), 1f))
                    gr.DrawPath(outline, facePath);

                if (!Enabled)
                    using (var dim = new SolidBrush(_ColorNotEnabled))
                        gr.FillPath(dim, facePath);

                // keyboard focus stays visible
                if (Focused && ShowFocusCues)
                    using (var focus = new Pen(Color.FromArgb(160, TextColor), 1f) { DashStyle = DashStyle.Dot })
                    using (var focusPath = RoundRect(RectangleF.Inflate(face, -3, -3), radius - 1))
                        gr.DrawPath(focus, focusPath);

                var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                string display = Text;
                int amppos = display.IndexOf('&');
                if (amppos != -1)
                    display = display.Remove(amppos, 1);
                using (var text = new SolidBrush(Enabled ? TextColor : TextColorNotEnabled))
                    gr.DrawString(display, Font, text, face, format);
            }
        }

        protected override void OnPaintBackground(PaintEventArgs pevent)
        {
            //base.OnPaintBackground(pevent);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _mouseover = true;
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _mouseover = false;
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs mevent)
        {
            _mousedown = true;
            base.OnMouseDown(mevent);
        }

        protected override void OnMouseUp(MouseEventArgs mevent)
        {
            _mousedown = false;
            base.OnMouseUp(mevent);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using MissionPlanner.Controls;
using MissionPlanner.Utilities;

namespace MissionPlanner.GCSViews.ConfigurationView
{
    /// <summary>
    /// Sarus: what this airframe can physically do. Parameters that ask for more blink red (SarusLimitsUI), and the
    /// in-flight alarm (SarusFlightAlarm) uses the alarm settings here. Nothing on this page changes a parameter.
    /// </summary>
    public class ConfigSarusLimits : UserControl, IActivate
    {
        private class Field
        {
            public string Label;
            public string Unit;
            public SarusEnvelope.Kind[] Kinds;
            public Func<SarusEnvelope, double?> Get;
            public Action<SarusEnvelope, double?> Set;
            public TextBox Box;
        }

        private static readonly SarusEnvelope.Kind[] FW = { SarusEnvelope.Kind.Plane, SarusEnvelope.Kind.QuadPlane };
        private static readonly SarusEnvelope.Kind[] HOVER = { SarusEnvelope.Kind.QuadPlane, SarusEnvelope.Kind.Copter };
        private static readonly SarusEnvelope.Kind[] RV = { SarusEnvelope.Kind.Rover };

        private readonly List<Field> fields = new List<Field>
        {
            new Field { Label = "Stall speed", Unit = "m/s", Kinds = FW, Get = e => e.StallSpeed, Set = (e, v) => e.StallSpeed = v },
            new Field { Label = "Maximum airspeed", Unit = "m/s", Kinds = FW, Get = e => e.MaxSpeed, Set = (e, v) => e.MaxSpeed = v },
            new Field { Label = "Maximum climb rate", Unit = "m/s", Kinds = FW, Get = e => e.MaxClimb, Set = (e, v) => e.MaxClimb = v },
            new Field { Label = "Maximum descent rate", Unit = "m/s", Kinds = FW, Get = e => e.MaxSink, Set = (e, v) => e.MaxSink = v },
            new Field { Label = "Maximum bank angle", Unit = "deg", Kinds = FW, Get = e => e.MaxBank, Set = (e, v) => e.MaxBank = v },
            new Field { Label = "Maximum nose-up pitch", Unit = "deg", Kinds = FW, Get = e => e.MaxPitchUp, Set = (e, v) => e.MaxPitchUp = v },
            new Field { Label = "Maximum nose-down pitch", Unit = "deg", Kinds = FW, Get = e => e.MaxPitchDown, Set = (e, v) => e.MaxPitchDown = v },
            new Field { Label = "Maximum lean angle (hover)", Unit = "deg", Kinds = HOVER, Get = e => e.MaxTilt, Set = (e, v) => e.MaxTilt = v },
            new Field { Label = "Maximum speed (hover)", Unit = "m/s", Kinds = HOVER, Get = e => e.MaxHoverSpeed, Set = (e, v) => e.MaxHoverSpeed = v },
            new Field { Label = "Maximum climb rate (hover)", Unit = "m/s", Kinds = HOVER, Get = e => e.MaxHoverClimb, Set = (e, v) => e.MaxHoverClimb = v },
            new Field { Label = "Maximum descent rate (hover)", Unit = "m/s", Kinds = HOVER, Get = e => e.MaxHoverSink, Set = (e, v) => e.MaxHoverSink = v },
            new Field { Label = "Maximum speed", Unit = "m/s", Kinds = RV, Get = e => e.MaxSpeed, Set = (e, v) => e.MaxSpeed = v },
            new Field { Label = "Smallest turn radius", Unit = "m", Kinds = RV, Get = e => e.MinTurnRadius, Set = (e, v) => e.MinTurnRadius = v },
        };

        private readonly Label title = new Label();
        private readonly Label intro = new Label();
        private readonly TableLayoutPanel grid = new TableLayoutPanel();
        private readonly CheckBox alarmOn = new CheckBox();
        private readonly TextBox alarmAlt = new TextBox(), alarmSpd = new TextBox(), alarmTrack = new TextBox(), alarmSecs = new TextBox();
        private readonly MyButton save = new MyButton();
        private readonly Label status = new Label();
        private readonly ListBox problems = new ListBox();
        private readonly Font titleFont = new Font("Segoe UI Semibold", 14f);
        private readonly Font headingFont = new Font("Segoe UI Semibold", 11f);
        private string key;
        private SarusEnvelope env;

        public ConfigSarusLimits()
        {
            AutoScroll = true;
            Padding = new Padding(16);

            var stack = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            title.Text = "Airframe limits";
            title.Font = titleFont;
            title.AutoSize = true;
            intro.Text = "Enter what this airframe can physically do. Any parameter that asks for more blinks red, here, in the " +
                         "parameter list and across the top of the window. Your values are never changed or refused. Leave a " +
                         "box empty to skip that check. The limits are stored for this flight controller only.";
            intro.MaximumSize = new Size(640, 0);
            intro.AutoSize = true;
            intro.Margin = new Padding(0, 4, 0, 12);

            grid.AutoSize = true;
            grid.ColumnCount = 3;
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 50));

            var alarmTitle = new Label
            {
                Text = "In-flight alarm", Font = headingFont, AutoSize = true, Margin = new Padding(0, 16, 0, 2)
            };
            var alarmIntro = new Label
            {
                Text = "During a mission, if the aircraft stays this far from what it is trying to do for longer than the time " +
                       "below, a siren sounds and you are asked to continue, hold or return. With no answer in 15 seconds the " +
                       "mission continues.",
                MaximumSize = new Size(640, 0), AutoSize = true, Margin = new Padding(0, 0, 0, 8)
            };
            alarmOn.Text = "Sound the alarm";
            alarmOn.AutoSize = true;
            var alarmGrid = new TableLayoutPanel { AutoSize = true, ColumnCount = 3 };
            alarmGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));
            alarmGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            alarmGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 50));
            AddRow(alarmGrid, "Altitude off by more than", alarmAlt, "m");
            AddRow(alarmGrid, "Airspeed off by more than", alarmSpd, "m/s");
            AddRow(alarmGrid, "Off the planned track by more than", alarmTrack, "m");
            AddRow(alarmGrid, "For longer than", alarmSecs, "s");

            save.Text = "Save limits";
            save.Size = new Size(140, 30);
            save.Margin = new Padding(0, 16, 0, 4);
            save.Click += (s, e) => Save();
            status.AutoSize = true;
            status.MaximumSize = new Size(640, 0);

            var probTitle = new Label
            {
                Text = "Parameters beyond these limits now", Font = headingFont, AutoSize = true,
                Margin = new Padding(0, 16, 0, 2)
            };
            problems.Width = 640;
            problems.Height = 120;
            problems.ForeColor = SarusLimitsUI.AlertRed;
            problems.AccessibleName = "Parameters beyond the airframe limits";

            stack.Controls.AddRange(new Control[] { title, intro, grid, alarmTitle, alarmIntro, alarmOn, alarmGrid, save, status, probTitle, problems });
            Controls.Add(stack);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                titleFont.Dispose();
                headingFont.Dispose();
            }
            base.Dispose(disposing);
        }

        /// <summary>
        /// Red for error text on the given background: the alert red where it reads (at least 4.5:1), otherwise the same
        /// red eased toward white, so it holds on the dark themes too
        /// </summary>
        private static Color ErrorText(Color back)
        {
            if (back.A < 255)
                back = Color.FromArgb(back.R, back.G, back.B);
            var red = SarusLimitsUI.AlertRed;
            for (double t = 0; t <= 1.0; t += 0.05)
            {
                var c = Color.FromArgb((int) Math.Round(red.R + (255 - red.R) * t),
                    (int) Math.Round(red.G + (255 - red.G) * t), (int) Math.Round(red.B + (255 - red.B) * t));
                if (ThemeManager.Contrast(c, back) >= 4.5)
                    return c;
            }
            return Color.White;
        }

        private static void AddRow(TableLayoutPanel t, string label, TextBox box, string unit)
        {
            box.Width = 80;
            box.AccessibleName = label + " in " + unit;
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 0, 6) });
            t.Controls.Add(box);
            t.Controls.Add(new Label { Text = unit, AutoSize = true, Anchor = AnchorStyles.Left });
        }

        public void Activate()
        {
            bool connected = MainV2.comPort?.BaseStream != null && MainV2.comPort.BaseStream.IsOpen;
            key = connected ? SarusLimitsUI.KeyForCurrent() : null;
            var kind = connected ? SarusLimitsUI.KindForCurrent() : SarusEnvelope.Kind.Other;
            env = key != null ? SarusEnvelope.Load(key) : new SarusEnvelope();

            grid.SuspendLayout();
            // Controls.Clear() only detaches; dispose the old rows so each visit does not leak them
            foreach (var old in grid.Controls.Cast<Control>().ToList())
            {
                grid.Controls.Remove(old);
                old.Dispose();
            }
            foreach (var f in fields)
            {
                f.Box = null;
                if (!f.Kinds.Contains(kind))
                    continue;
                f.Box = new TextBox();
                var v = f.Get(env);
                f.Box.Text = v?.ToString(CultureInfo.CurrentCulture) ?? "";
                AddRow(grid, f.Label, f.Box, f.Unit);
            }
            grid.ResumeLayout();

            alarmOn.Checked = env.AlarmEnabled;
            alarmAlt.Text = env.AlarmAltitudeError.ToString(CultureInfo.CurrentCulture);
            alarmSpd.Text = env.AlarmAirspeedError.ToString(CultureInfo.CurrentCulture);
            alarmTrack.Text = env.AlarmTrackError.ToString(CultureInfo.CurrentCulture);
            alarmSecs.Text = env.AlarmAfterSeconds.ToString(CultureInfo.CurrentCulture);

            save.Enabled = key != null;
            status.ForeColor = ForeColor;
            status.Text = key == null
                ? "Connect to the aircraft to enter its limits."
                : "Flight controller " + key + ", " + kind + ".";
            ShowProblems();
            ThemeManager.ApplyThemeTo(this);
            problems.ForeColor = ErrorText(problems.BackColor);
        }

        private void ShowProblems()
        {
            problems.Items.Clear();
            if (key == null || !env.AnyLimitEntered)
                return;
            var mav = MainV2.comPort.MAV;
            var list = env.CheckAll(SarusLimitsUI.KindForCurrent(),
                mav.param.ToArray().Select(p => new KeyValuePair<string, double>(p.Name, p.Value)));
            foreach (var v in list)
                problems.Items.Add(v.Message);
            if (list.Count == 0)
                problems.Items.Add("None.");
        }

        private static bool TryRead(TextBox box, out double? value)
        {
            value = null;
            var t = box.Text.Trim();
            if (t.Length == 0)
                return true;
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.CurrentCulture, out var d) && d > 0 && d < 100000)
            {
                value = d;
                return true;
            }
            return false;
        }

        private void Save()
        {
            if (key == null)
                return;
            var bad = new List<string>();
            foreach (var f in fields.Where(f => f.Box != null))
            {
                if (TryRead(f.Box, out var v))
                    f.Set(env, v);
                else
                    bad.Add(f.Label);
            }
            double? a, s, t, n;
            if (!TryRead(alarmAlt, out a) || a == null) bad.Add("altitude alarm");
            if (!TryRead(alarmSpd, out s) || s == null) bad.Add("airspeed alarm");
            if (!TryRead(alarmTrack, out t) || t == null) bad.Add("track alarm");
            if (!TryRead(alarmSecs, out n) || n == null || n < 1) bad.Add("alarm time");

            if (bad.Count > 0)
            {
                status.ForeColor = ErrorText(status.BackColor);
                status.Text = "Not saved. Use a positive number, or leave the box empty, for: " + string.Join(", ", bad) + ".";
                return;
            }

            env.AlarmEnabled = alarmOn.Checked;
            env.AlarmAltitudeError = a.Value;
            env.AlarmAirspeedError = s.Value;
            env.AlarmTrackError = t.Value;
            env.AlarmAfterSeconds = (int) Math.Round(n.Value);
            try
            {
                env.Save(key);
                SarusLimitsUI.Reload();
                status.ForeColor = ForeColor;
                status.Text = "Saved " + DateTime.Now.ToShortTimeString() + " for flight controller " + key + ".";
            }
            catch (Exception ex)
            {
                status.ForeColor = ErrorText(status.BackColor);
                status.Text = "Could not save: " + ex.Message;
            }
            ShowProblems();
        }
    }
}

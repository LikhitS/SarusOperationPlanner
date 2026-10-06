using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using log4net;

namespace MissionPlanner.Utilities
{
    /// <summary>
    /// Sarus airframe limits on screen: the blinking red banner across the main window while any parameter asks for
    /// more than the airframe can do, and the shared state the Airframe Limits page and the parameter list read.
    /// </summary>
    public static class SarusLimitsUI
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public static readonly Color AlertRed = Color.FromArgb(0xB3, 0x26, 0x1E);
        public static readonly Color AlertRedDim = Color.FromArgb(0x6E, 0x17, 0x12);

        private static Panel banner;
        private static Label bannerText;
        private static string dismissedFor;
        private static bool blinkOn;
        private static string cachedKey;
        private static SarusEnvelope cachedEnvelope;

        /// <summary>current violations on the connected aircraft; read by the parameter list</summary>
        public static List<SarusEnvelope.Violation> Current { get; private set; } = new List<SarusEnvelope.Violation>();

        /// <summary>raised (on the UI thread) every half second while violations exist, for anything that blinks</summary>
        public static event Action<bool> Blink;

        public static string KeyForCurrent()
        {
            var mav = MainV2.comPort?.MAV;
            if (mav == null)
                return null;
            return SarusEnvelope.KeyFor(mav.cs.uid2, mav.sysid, mav.cs.firmware);
        }

        /// <summary>limits for the connected aircraft (cached; Reload() after the page saves)</summary>
        public static SarusEnvelope EnvelopeForCurrent()
        {
            var key = KeyForCurrent();
            if (key == null)
                return new SarusEnvelope();
            if (key != cachedKey || cachedEnvelope == null)
            {
                cachedEnvelope = SarusEnvelope.Load(key);
                cachedKey = key;
            }
            return cachedEnvelope;
        }

        public static void Reload()
        {
            cachedKey = null;
            cachedEnvelope = null;
            dismissedFor = null;
        }

        public static SarusEnvelope.Kind KindForCurrent()
        {
            var mav = MainV2.comPort.MAV;
            return SarusEnvelope.KindOf(mav.cs.firmware,
                n => mav.param.ContainsKey(n) ? (double?) mav.param[n].Value : null);
        }

        /// <summary>check one value as the user types it, before it is written</summary>
        public static List<SarusEnvelope.Violation> CheckValue(string param, double value)
        {
            if (MainV2.comPort?.BaseStream == null || !MainV2.comPort.BaseStream.IsOpen)
                return new List<SarusEnvelope.Violation>();
            return EnvelopeForCurrent().Check(KindForCurrent(), param, value).ToList();
        }

        public static void Attach(MainV2 form)
        {
            banner = new Panel { Dock = DockStyle.Top, Height = 34, Visible = false, BackColor = AlertRed, Padding = new Padding(10, 0, 6, 0) };
            bannerText = new Label
            {
                Dock = DockStyle.Fill, ForeColor = Color.White, TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold), AutoEllipsis = true, AccessibleRole = AccessibleRole.Alert
            };
            var details = new Button
            {
                Text = "Details", Dock = DockStyle.Right, Width = 80, FlatStyle = FlatStyle.Flat, ForeColor = Color.White,
                AccessibleName = "Show every parameter beyond the airframe limits"
            };
            details.FlatAppearance.BorderColor = Color.White;
            var hide = new Button
            {
                Text = "Hide", Dock = DockStyle.Right, Width = 70, FlatStyle = FlatStyle.Flat, ForeColor = Color.White,
                AccessibleName = "Hide this alert until the parameters change again"
            };
            hide.FlatAppearance.BorderColor = Color.White;
            details.Click += (s, e) => CustomMessageBox.Show(
                string.Join("\n", Current.Select(v => v.Message)) +
                "\n\nThe values were set as you asked; Sarus does not change them. Check the airframe limits page or the parameters.",
                "Parameters beyond the airframe limits");
            hide.Click += (s, e) =>
            {
                dismissedFor = Signature(Current);
                banner.Visible = false;
            };
            banner.Controls.Add(bannerText);
            banner.Controls.Add(details);
            banner.Controls.Add(hide);

            // below the menu bar, above the screens
            var top = form.Controls.Cast<Control>().FirstOrDefault(c => c.Name == "panel1");
            form.Controls.Add(banner);
            if (top != null)
                form.Controls.SetChildIndex(banner, form.Controls.GetChildIndex(top));

            var check = new Timer { Interval = 1000 };
            check.Tick += (s, e) => Evaluate();
            check.Start();

            var blink = new Timer { Interval = 500 };
            blink.Tick += (s, e) =>
            {
                // always ticks: the parameter list also blinks values typed but not yet written
                blinkOn = !blinkOn;
                if (banner.Visible)
                    banner.BackColor = blinkOn ? AlertRed : AlertRedDim;
                Blink?.Invoke(blinkOn);
            };
            blink.Start();
        }

        private static string Signature(IEnumerable<SarusEnvelope.Violation> v) =>
            string.Join("|", v.Select(x => x.Param + "=" + x.Value.ToString("R")));

        private static void Evaluate()
        {
            try
            {
                var port = MainV2.comPort;
                if (port?.BaseStream == null || !port.BaseStream.IsOpen || port.MAV.param.Count == 0)
                {
                    Current = new List<SarusEnvelope.Violation>();
                }
                else
                {
                    var env = EnvelopeForCurrent();
                    Current = env.AnyLimitEntered
                        ? env.CheckAll(KindForCurrent(),
                            port.MAV.param.Select(p => new KeyValuePair<string, double>(p.Name, p.Value)))
                        : new List<SarusEnvelope.Violation>();
                }

                if (Current.Count == 0)
                {
                    banner.Visible = false;
                    dismissedFor = null;
                    return;
                }

                bannerText.Text = Current.Count == 1
                    ? "Beyond airframe limits: " + Current[0].Message
                    : "Beyond airframe limits: " + Current[0].Message + "  (and " + (Current.Count - 1) + " more)";
                var sig = Signature(Current);
                if (sig != dismissedFor && !banner.Visible)
                {
                    log.Warn("Sarus airframe limits: " + string.Join("; ", Current.Select(v => v.Message)));
                    banner.Visible = true;
                }
            }
            catch (Exception ex)
            {
                log.Error(ex);
            }
        }
    }
}

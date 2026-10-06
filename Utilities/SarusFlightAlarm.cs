using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Media;
using System.Windows.Forms;
using log4net;

namespace MissionPlanner.Utilities
{
    /// <summary>
    /// Sarus in-flight alarm. During a mission (AUTO or GUIDED, armed) it compares what the aircraft is trying to do
    /// with what it achieves: altitude, airspeed and track, from NAV_CONTROLLER_OUTPUT. When one stays beyond the
    /// limit set on the Airframe Limits page for longer than the set time and is not getting smaller, a siren sounds
    /// and the pilot is asked to continue, hold or return. With no answer in 15 seconds the mission simply continues
    /// with the parameters as set: the alarm never changes anything on its own.
    /// </summary>
    public static class SarusFlightAlarm
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const int AnswerSeconds = 15;
        public const int QuietAfterContinueSeconds = 60;

        private class Watch
        {
            public string What;
            public string Unit;
            public DateTime? Since;   // when it first went beyond the limit
            public double StartError; // |error| at that moment
        }

        private static readonly Watch alt = new Watch { What = "altitude", Unit = "m" };
        private static readonly Watch spd = new Watch { What = "airspeed", Unit = "m/s" };
        private static readonly Watch trk = new Watch { What = "track", Unit = "m" };
        private static DateTime quietUntil = DateTime.MinValue;

        // airspeed is only judged once the aircraft has flown on its wings in this phase, so a VTOL take-off,
        // hover or ground roll (target airspeed far above actual, by design) never counts
        private static bool wingborne;
        private static DateTime slowSince = DateTime.MaxValue;

        // take-off, landing and return legs: errors there are large by design, so nothing is judged
        private static readonly HashSet<ushort> transitItems = new HashSet<ushort>
        {
            (ushort) MAVLink.MAV_CMD.RETURN_TO_LAUNCH, (ushort) MAVLink.MAV_CMD.LAND, (ushort) MAVLink.MAV_CMD.TAKEOFF,
            (ushort) MAVLink.MAV_CMD.VTOL_TAKEOFF, (ushort) MAVLink.MAV_CMD.VTOL_LAND, (ushort) MAVLink.MAV_CMD.PAYLOAD_PLACE
        };
        private static Form open;
        private static SoundPlayer siren;

        // mission item commands fetched from the aircraft when the app has no copy of the mission; null while
        // a fetch is under way. Cleared on the ground, since the mission may change between flights.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(uint, byte, int), ushort?> fetched =
            new System.Collections.Concurrent.ConcurrentDictionary<(uint, byte, int), ushort?>();

        /// <summary>for tests: raised with the alarm text when the alarm sounds</summary>
        public static event Action<string> Raised;

        /// <summary>for tests: raised with the pilot's choice (or "continue (no answer)")</summary>
        public static event Action<string> Answered;

        public static void Attach(MainV2 form)
        {
            var t = new Timer { Interval = 1000 };
            t.Tick += (s, e) =>
            {
                try
                {
                    Tick(form);
                }
                catch (Exception ex)
                {
                    log.Error(ex);
                }
            };
            t.Start();
        }

        private static bool InMission(string mode)
        {
            mode = (mode ?? "").ToUpperInvariant();
            return mode == "AUTO" || mode == "GUIDED";
        }

        private static void Reset()
        {
            alt.Since = spd.Since = trk.Since = null;
            wingborne = false;
            slowSince = DateTime.MaxValue;
        }

        private static void Tick(MainV2 form)
        {
            var port = MainV2.comPort;
            if (open != null || port?.BaseStream == null || !port.BaseStream.IsOpen)
                return;
            var cs = port.MAV.cs;
            uint sysid = port.MAV.sysid;
            byte compid = port.MAV.compid;
            if (!cs.armed || !InMission(cs.mode))
            {
                Reset();
                if (!cs.armed)
                    fetched.Clear();
                return;
            }

            var env = SarusLimitsUI.EnvelopeForCurrent();
            if (!env.AlarmEnabled || DateTime.Now < quietUntil)
                return;

            // the mission item being flown; asked from the aircraft when the app has no copy of the mission
            ushort? command;
            if (port.MAV.wps.TryGetValue((int) cs.wpno, out var item))
                command = item.command;
            else
                command = FetchedCommand(port, sysid, compid, (int) cs.wpno);
            if (command == null)
                return; // not known yet: judge nothing rather than judge a landing as cruise
            if (transitItems.Contains(command.Value))
            {
                Reset();
                return;
            }

            var kind = SarusLimitsUI.KindForCurrent();
            double minAirspeed = 8;
            foreach (var n in new[] { "AIRSPEED_MIN", "ARSPD_FBW_MIN" })
                if (port.MAV.param.ContainsKey(n) && port.MAV.param[n].Value > 0)
                {
                    minAirspeed = port.MAV.param[n].Value;
                    break;
                }
            double airspeed = cs.airspeed / (CurrentState.multiplierspeed == 0 ? 1 : CurrentState.multiplierspeed); // m/s
            if (airspeed > minAirspeed * 0.9)
            {
                wingborne = true;
                slowSince = DateTime.MaxValue;
            }
            else if (airspeed < 3)
            {
                if (slowSince == DateTime.MaxValue)
                    slowSince = DateTime.Now;
                else if ((DateTime.Now - slowSince).TotalSeconds > 5)
                    wingborne = false; // hovering again
            }

            double altErr = Math.Abs(cs.alt_error / (CurrentState.multiplieralt == 0 ? 1 : CurrentState.multiplieralt));
            double spdErr = Math.Abs(cs.aspd_error / (CurrentState.multiplierspeed == 0 ? 1 : CurrentState.multiplierspeed));
            double trkErr = Math.Abs(cs.xtrack_error);

            var hits = new List<string>();
            if (kind != SarusEnvelope.Kind.Rover && Update(alt, altErr, env.AlarmAltitudeError, env.AlarmAfterSeconds))
                hits.Add(Describe(alt, altErr, env));
            if ((kind == SarusEnvelope.Kind.Plane || kind == SarusEnvelope.Kind.QuadPlane) && wingborne &&
                cs.vtol_state != 3 && Update(spd, spdErr, env.AlarmAirspeedError, env.AlarmAfterSeconds))
                hits.Add(Describe(spd, spdErr, env));
            if (Update(trk, trkErr, env.AlarmTrackError, env.AlarmAfterSeconds))
                hits.Add(Describe(trk, trkErr, env));

            if (hits.Count > 0)
                Show(form, hits, port, sysid, compid, kind);
        }

        private static ushort? FetchedCommand(MAVLinkInterface port, uint sysid, byte compid, int seq)
        {
            var key = (sysid, compid, seq);
            if (fetched.TryGetValue(key, out var command))
                return command;
            fetched[key] = null;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    fetched[key] = port.getWP(sysid, compid, (ushort) seq).id;
                }
                catch (Exception ex)
                {
                    // unknown item: judge it like any other rather than never alarm
                    log.Warn("Sarus flight alarm: mission item " + seq + " not read: " + ex.Message);
                    fetched[key] = 0;
                }
            });
            return null;
        }

        /// <summary>
        /// True once the error has stayed beyond the limit for the set time without shrinking by a fifth. An error that
        /// is closing (climbing to a new altitude, turning onto a new leg) never raises the alarm.
        /// </summary>
        private static bool Update(Watch w, double error, double limit, int seconds)
        {
            if (limit <= 0 || error <= limit)
            {
                w.Since = null;
                return false;
            }
            if (w.Since == null)
            {
                w.Since = DateTime.Now;
                w.StartError = error;
                return false;
            }
            if (error < w.StartError * 0.8)
            {
                // getting there: start the clock again from here
                w.Since = DateTime.Now;
                w.StartError = error;
                return false;
            }
            return (DateTime.Now - w.Since.Value).TotalSeconds >= seconds;
        }

        private static string Describe(Watch w, double error, SarusEnvelope env)
        {
            var secs = (int) (DateTime.Now - w.Since.Value).TotalSeconds;
            return $"The {w.What} has been {error:0.#} {w.Unit} away from its target for {secs} s and is not closing.";
        }

        private static void Show(MainV2 owner, List<string> hits, MAVLinkInterface port, uint sysid, byte compid,
            SarusEnvelope.Kind kind)
        {
            // the aircraft is named, and the answer goes to it even if another aircraft is selected meanwhile
            string text = "Aircraft " + sysid + ": " + string.Join("\n", hits);
            log.Warn("Sarus flight alarm: " + text.Replace("\n", " "));
            Raised?.Invoke(text);
            StartSiren();

            int left = AnswerSeconds;
            var headFont = new Font("Segoe UI Semibold", 13f);
            var bodyFont = new Font("Segoe UI", 10.5f);
            var countFont = new Font("Segoe UI Semibold", 10.5f);
            var buttonFont = new Font("Segoe UI Semibold", 11f);
            // shown without an owner, so it stays on screen when the main window is minimised; listed in the taskbar
            // so it can always be found
            var f = new AlarmForm
            {
                Text = "Sarus flight alarm", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.Manual,
                TopMost = true, ShowInTaskbar = true, MinimizeBox = false, MaximizeBox = false, ControlBox = false,
                BackColor = Color.FromArgb(0x1C, 0x22, 0x29), ForeColor = Color.White, KeyPreview = true
            };
            var head = new Label
            {
                Text = "The aircraft is not achieving its set parameters", Dock = DockStyle.Top, Height = 40,
                Font = headFont, ForeColor = Color.White, BackColor = SarusLimitsUI.AlertRed,
                TextAlign = ContentAlignment.MiddleCenter
            };
            // every alarm line is shown in full: the label grows to fit, and the rest of the dialog moves down
            var body = new Label
            {
                Text = text, Location = new Point(20, 52), AutoSize = true, MaximumSize = new Size(520, 0), Font = bodyFont,
                AccessibleRole = AccessibleRole.Alert
            };
            var count = new Label { AutoSize = true, MaximumSize = new Size(520, 0), Font = countFont };
            void SetCount() => count.Text = "With no answer the mission continues as planned in " + left + " s.";
            SetCount();

            // no button takes keyboard focus and none is the default or cancel button, so a key pressed while the
            // pilot is busy elsewhere (Enter, Escape, Space) can never answer the alarm; only a click does
            Button Make(string label, int x)
            {
                var b = new Button
                {
                    Text = label, Location = new Point(x, 0), Size = new Size(160, 48), FlatStyle = FlatStyle.Flat,
                    Font = buttonFont, ForeColor = Color.White, BackColor = Color.FromArgb(0x2B, 0x34, 0x3E), TabStop = false
                };
                b.FlatAppearance.BorderColor = Color.FromArgb(0x8A, 0x96, 0xA3);
                return b;
            }
            var cont = Make("Continue plan", 20);
            var hold = Make("Hold position", 200);
            var home = Make("Return home", 380);
            f.Controls.AddRange(new Control[] { head, body, count, cont, hold, home });
            f.KeyDown += (s, e) => e.SuppressKeyPress = e.Handled = true;

            int y = body.Bottom + 12;
            count.Location = new Point(20, y);
            y = count.Bottom + 18;
            foreach (var b in new[] { cont, hold, home })
                b.Top = y;
            f.ClientSize = new Size(560, y + 48 + 20);

            // centred on the screen the app is on, whether the app is minimised or not
            var area = owner != null && !owner.IsDisposed && owner.WindowState != FormWindowState.Minimized
                ? owner.Bounds
                : Screen.FromControl(owner ?? (Control) f).WorkingArea;
            var screen = Screen.FromRectangle(area).WorkingArea;
            f.Location = new Point(
                Math.Max(screen.Left, Math.Min(screen.Right - f.Width, area.Left + (area.Width - f.Width) / 2)),
                Math.Max(screen.Top, Math.Min(screen.Bottom - f.Height, area.Top + (area.Height - f.Height) / 2)));

            var tick = new Timer { Interval = 1000 };
            bool done = false;
            void Finish(string choice)
            {
                if (done)
                    return;
                done = true;
                tick.Stop();
                StopSiren();
                log.Warn("Sarus flight alarm answer: " + choice);
                if (choice == "hold")
                    SetMode(port, sysid, compid, HoldMode(port.MAVlist[sysid, compid].cs, kind));
                else if (choice == "return")
                    SetMode(port, sysid, compid, "RTL");
                if (choice.StartsWith("continue"))
                    quietUntil = DateTime.Now.AddSeconds(QuietAfterContinueSeconds);
                Reset();
                Answered?.Invoke(choice);
                f.Close();
            }
            cont.Click += (s, e) => Finish("continue");
            hold.Click += (s, e) => Finish("hold");
            home.Click += (s, e) => Finish("return");
            tick.Tick += (s, e) =>
            {
                left--;
                SetCount();
                if (left <= 0)
                    Finish("continue (no answer)");
            };
            // Alt+F4 must not close it unanswered; Windows shutting down or the app exiting may
            f.FormClosing += (s, e) =>
            {
                if (!done && e.CloseReason == CloseReason.UserClosing)
                    e.Cancel = true;
            };
            f.FormClosed += (s, e) =>
            {
                tick.Dispose();
                StopSiren();
                open = null;
                headFont.Dispose();
                bodyFont.Dispose();
                countFont.Dispose();
                buttonFont.Dispose();
            };

            open = f;
            tick.Start();
            // shown without taking focus from whatever the pilot is doing
            f.Show();
        }

        // a top-most window that appears without taking keyboard focus
        private class AlarmForm : Form
        {
            protected override bool ShowWithoutActivation => true;

            protected override CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.ExStyle |= 0x08000000 | 0x00000008; // WS_EX_NOACTIVATE, WS_EX_TOPMOST
                    return cp;
                }
            }
        }

        private static string HoldMode(CurrentState cs, SarusEnvelope.Kind kind)
        {
            switch (kind)
            {
                case SarusEnvelope.Kind.QuadPlane:
                    return cs.vtol_state == 3 ? "QLOITER" : "LOITER"; // 3 = hovering
                case SarusEnvelope.Kind.Rover:
                    return "HOLD";
                default:
                    return "LOITER";
            }
        }

        // asks for the mode with an acknowledgement, off the UI thread, and tells the pilot if the aircraft refuses
        private static void SetMode(MAVLinkInterface port, uint sysid, byte compid, string mode)
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                string problem = null;
                try
                {
                    var req = new MAVLink.mavlink_set_mode_t();
                    if (!port.translateMode(sysid, compid, mode, ref req))
                        problem = "This aircraft has no " + mode + " mode.";
                    else if (!port.doCommand(sysid, compid, MAVLink.MAV_CMD.DO_SET_MODE, req.base_mode, req.custom_mode,
                                 0, 0, 0, 0, 0, true))
                        problem = "The aircraft did not accept " + mode + ".";
                }
                catch (Exception ex)
                {
                    log.Error(ex);
                    problem = "The change to " + mode + " failed: " + ex.Message;
                }
                log.Warn("Sarus flight alarm: aircraft " + sysid + " " + mode + " -> " + (problem ?? "accepted"));
                if (problem != null)
                    MainV2.instance?.BeginInvoke((Action) (() => CustomMessageBox.Show(
                        "Aircraft " + sysid + ": " + problem + " Take control with the transmitter.", "Sarus flight alarm")));
            });
        }

        private static void StartSiren()
        {
            try
            {
                if (siren == null)
                    siren = new SoundPlayer(new MemoryStream(SirenWav()));
                siren.PlayLooping();
            }
            catch (Exception ex)
            {
                log.Error("siren", ex);
                SystemSounds.Exclamation.Play();
            }
        }

        private static void StopSiren()
        {
            try
            {
                siren?.Stop();
            }
            catch
            {
            }
        }

        /// <summary>two-tone siren, 0.4 s at 880 Hz then 0.4 s at 620 Hz, 16-bit mono PCM</summary>
        private static byte[] SirenWav()
        {
            const int rate = 22050;
            var samples = new List<short>();
            foreach (var hz in new[] { 880.0, 620.0 })
            {
                int n = (int) (rate * 0.4);
                for (int i = 0; i < n; i++)
                {
                    // short fade at each end so the tone does not click
                    double env = Math.Min(1.0, Math.Min(i, n - i) / (rate * 0.01));
                    samples.Add((short) (Math.Sin(2 * Math.PI * hz * i / rate) * 12000 * env));
                }
            }
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            int bytes = samples.Count * 2;
            w.Write(new[] { 'R', 'I', 'F', 'F' });
            w.Write(36 + bytes);
            w.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
            w.Write(16);
            w.Write((short) 1);
            w.Write((short) 1);
            w.Write(rate);
            w.Write(rate * 2);
            w.Write((short) 2);
            w.Write((short) 16);
            w.Write(new[] { 'd', 'a', 't', 'a' });
            w.Write(bytes);
            foreach (var s in samples)
                w.Write(s);
            w.Flush();
            return ms.ToArray();
        }
    }
}

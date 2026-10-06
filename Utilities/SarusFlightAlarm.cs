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
            if (!cs.armed || !InMission(cs.mode))
            {
                Reset();
                return;
            }

            var env = SarusLimitsUI.EnvelopeForCurrent();
            if (!env.AlarmEnabled || DateTime.Now < quietUntil)
                return;

            // the mission item being flown, when the app knows the mission
            if (port.MAV.wps.TryGetValue((int) cs.wpno, out var item) && transitItems.Contains(item.command))
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
            if (cs.airspeed > minAirspeed * 0.9)
            {
                wingborne = true;
                slowSince = DateTime.MaxValue;
            }
            else if (cs.airspeed < 3)
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
                Show(form, hits);
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

        private static void Show(MainV2 owner, List<string> hits)
        {
            string text = string.Join("\n", hits);
            log.Warn("Sarus flight alarm: " + text.Replace("\n", " "));
            Raised?.Invoke(text);
            StartSiren();

            int left = AnswerSeconds;
            var f = new Form
            {
                Text = "Sarus flight alarm", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
                TopMost = true, ShowInTaskbar = false, MinimizeBox = false, MaximizeBox = false, ControlBox = false,
                ClientSize = new Size(560, 250), BackColor = Color.FromArgb(0x1C, 0x22, 0x29), ForeColor = Color.White,
                KeyPreview = true
            };
            var head = new Label
            {
                Text = "The aircraft is not achieving its set parameters", Dock = DockStyle.Top, Height = 40,
                Font = new Font("Segoe UI Semibold", 13f), ForeColor = Color.White, BackColor = SarusLimitsUI.AlertRed,
                TextAlign = ContentAlignment.MiddleCenter
            };
            var body = new Label
            {
                Text = text, Location = new Point(20, 52), Size = new Size(520, 70), Font = new Font("Segoe UI", 10.5f),
                AccessibleRole = AccessibleRole.Alert
            };
            var count = new Label
            {
                Location = new Point(20, 126), Size = new Size(520, 24), Font = new Font("Segoe UI Semibold", 10.5f)
            };
            void SetCount() => count.Text = "With no answer the mission continues as planned in " + left + " s.";
            SetCount();

            Button Make(string label, int x)
            {
                var b = new Button
                {
                    Text = label, Location = new Point(x, 170), Size = new Size(160, 48), FlatStyle = FlatStyle.Flat,
                    Font = new Font("Segoe UI Semibold", 11f), ForeColor = Color.White, BackColor = Color.FromArgb(0x2B, 0x34, 0x3E)
                };
                b.FlatAppearance.BorderColor = Color.FromArgb(0x8A, 0x96, 0xA3);
                return b;
            }
            var cont = Make("Continue plan", 20);
            var hold = Make("Hold position", 200);
            var home = Make("Return home", 380);
            f.Controls.AddRange(new Control[] { head, body, count, cont, hold, home });
            f.AcceptButton = cont;
            f.CancelButton = cont;

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
                try
                {
                    if (choice == "hold")
                        SetMode(HoldMode());
                    else if (choice == "return")
                        SetMode("RTL");
                }
                catch (Exception ex)
                {
                    log.Error(ex);
                    CustomMessageBox.Show("The mode change failed: " + ex.Message, "Sarus flight alarm");
                }
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
            f.FormClosed += (s, e) =>
            {
                tick.Dispose();
                StopSiren();
                open = null;
            };

            open = f;
            tick.Start();
            f.Show(owner);
            f.Activate();
        }

        private static string HoldMode()
        {
            var cs = MainV2.comPort.MAV.cs;
            switch (SarusLimitsUI.KindForCurrent())
            {
                case SarusEnvelope.Kind.QuadPlane:
                    return cs.vtol_state == 3 ? "QLOITER" : "LOITER"; // 3 = hovering
                case SarusEnvelope.Kind.Rover:
                    return "HOLD";
                default:
                    return "LOITER";
            }
        }

        private static void SetMode(string mode)
        {
            var port = MainV2.comPort;
            port.setMode(port.MAV.sysid, port.MAV.compid, mode);
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

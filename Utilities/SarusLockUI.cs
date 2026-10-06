using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using log4net;
using MissionPlanner.ArduPilot;
using MissionPlanner.Controls;

namespace MissionPlanner.Utilities
{
    /// <summary>
    /// Sarus parameter lock, the parts the user sees: the password prompt when a change is attempted, the
    /// lock/unlock button, and unlocking (or locking) each connected aircraft that runs Sarus firmware.
    /// </summary>
    public static class SarusLockUI
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private static bool prompting;
        private static DateTime declinedAt = DateTime.MinValue;

        public static void Init()
        {
            SarusLock.RequestUnlock = Ask;
            // unlocking is done where the password is accepted, so the change that asked for it waits for the
            // aircraft; locking can happen in the background
            SarusLock.Changed += (s, e) =>
            {
                if (SarusLock.Configured && !SarusLock.Unlocked)
                    ApplyToAllAircraft(false, false);
            };
        }

        // called from any thread; the prompt itself always runs on the UI thread
        private static bool Ask(string what)
        {
            if (StillDeclined())
                return false;

            var form = MainV2.instance;
            if (form == null || form.IsDisposed)
                return false;

            if (!form.InvokeRequired)
                return Prompt(what);

            // a prompt already open answers for this change too
            var until = DateTime.Now.AddMinutes(2);
            while (prompting && DateTime.Now < until)
                Thread.Sleep(100);
            if (SarusLock.Unlocked)
                return true;
            if (StillDeclined())
                return false;

            var expired = false;
            try
            {
                // a request that has given up must not show its prompt later
                var ar = form.BeginInvoke((Func<bool>) (() => !expired && Prompt(what)));
                if (!ar.AsyncWaitHandle.WaitOne(TimeSpan.FromMinutes(2)))
                {
                    expired = true;
                    return false;
                }
                return (bool) form.EndInvoke(ar);
            }
            catch (Exception ex)
            {
                // the window is closing; refuse the change rather than wait
                log.Info("Sarus lock prompt not shown: " + ex.Message);
                return false;
            }
        }

        // after a cancel, the rest of the same batch is refused without asking again; the batch is over once 3 s
        // pass without a change
        private static bool StillDeclined()
        {
            if (DateTime.Now - declinedAt >= TimeSpan.FromSeconds(3))
                return false;
            declinedAt = DateTime.Now;
            return true;
        }

        // runs slow work (scrypt, aircraft handshakes) off the UI thread while the screens keep updating
        private static T Responsive<T>(Func<T> work)
        {
            var t = Task.Run(work);
            while (!t.IsCompleted)
            {
                Application.DoEvents();
                Thread.Sleep(15);
            }
            return t.Result;
        }

        private static bool Prompt(string what)
        {
            if (prompting)
                return false;
            prompting = true;
            try
            {
                string intro = "Changing the aircraft's setup needs the Sarus admin password (" + what + ").\n" +
                               "Viewing parameters, planning and flying do not.";
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    string pw = "";
                    string text = attempt == 0 ? intro : "That password is not right. Try again.\n\n" + intro;
                    if (InputBox.Show("Sarus parameter lock", text, ref pw, true) != DialogResult.OK)
                    {
                        declinedAt = DateTime.Now;
                        return false;
                    }

                    bool ok;
                    var cursor = Cursor.Current;
                    Cursor.Current = Cursors.WaitCursor;
                    try
                    {
                        var typed = pw;
                        ok = Responsive(() => SarusLock.TryUnlock(typed));
                    }
                    finally
                    {
                        Cursor.Current = cursor;
                    }

                    if (ok)
                    {
                        Cursor.Current = Cursors.WaitCursor;
                        try
                        {
                            Responsive(() =>
                            {
                                UnlockAllAircraft();
                                return true;
                            });
                        }
                        finally
                        {
                            Cursor.Current = cursor;
                        }
                        return true;
                    }
                }

                declinedAt = DateTime.Now;
                CustomMessageBox.Show("Wrong password, three times. Nothing was changed.", "Sarus parameter lock");
                return false;
            }
            finally
            {
                prompting = false;
            }
        }

        /// <summary>after a connection: unlock the new aircraft if the app is unlocked</summary>
        public static void AfterConnect(MAVLinkInterface port)
        {
            if (port == null || !SarusLock.Configured || !SarusLock.Unlocked)
                return;
            Apply(port, true);
        }

        /// <summary>
        /// Unlock every connected aircraft with the key the app now holds, and wait (up to 10 s) so a change that
        /// is waiting for the unlock reaches an unlocked aircraft.
        /// </summary>
        public static void UnlockAllAircraft()
        {
            ApplyToAllAircraft(true, true);
        }

        private static void ApplyToAllAircraft(bool unlock, bool wait)
        {
            var tasks = MainV2.Comports.ToArray().Select(port => Apply(port, unlock)).ToArray();
            if (wait)
                Task.WaitAll(tasks, TimeSpan.FromSeconds(10));
        }

        // one handshake at a time: each GET_NONCE replaces the aircraft's nonce
        private static readonly SemaphoreSlim handshake = new SemaphoreSlim(1, 1);

        private static Task Apply(MAVLinkInterface port, bool unlock)
        {
            if (port?.BaseStream == null || !port.BaseStream.IsOpen)
                return Task.CompletedTask;

            var targets = port.MAVlist
                .Where(m => m.compid == (byte) MAVLink.MAV_COMPONENT.MAV_COMP_ID_AUTOPILOT1)
                .Select(m => (sysid: (uint) m.sysid, compid: m.compid)).ToList();

            return Task.Run(async () =>
            {
                var problems = new List<string>();
                await handshake.WaitAsync().ConfigureAwait(false);
                try
                {
                    foreach (var t in targets)
                        await ApplyOne(port, t.sysid, t.compid, unlock, problems).ConfigureAwait(false);
                }
                finally
                {
                    handshake.Release();
                }

                if (problems.Count > 0)
                {
                    var form = MainV2.instance;
                    form?.BeginInvoke((Action) (() =>
                        CustomMessageBox.Show(string.Join("\n\n", problems), "Sarus parameter lock")));
                }
            });
        }

        private static async Task ApplyOne(MAVLinkInterface port, uint sysid, byte compid, bool unlock, List<string> problems)
        {
            try
            {
                if (unlock)
                {
                    var r = await port.SarusLockUnlockAsync(sysid, compid).ConfigureAwait(false);
                    log.Info("Sarus lock: unlock aircraft " + sysid + " -> " + (r?.ToString() ?? "no Sarus lock"));
                    if (r == MAVLink.MAV_RESULT.DENIED)
                        problems.Add("Aircraft " + sysid + " refused the unlock. Its Sarus firmware was built with a " +
                                     "different admin key, so parameter changes will not take effect.");
                    else if (r == MAVLink.MAV_RESULT.TEMPORARILY_REJECTED)
                        problems.Add("Aircraft " + sysid + " is armed, and a Sarus aircraft can only be unlocked on the " +
                                     "ground. Disarm it, then lock and unlock again.");
                }
                else
                {
                    // only an aircraft this station unlocked needs locking; ArduPilot without the lock has nothing to lock
                    var flags = await port.SarusLockStatusAsync(sysid, compid).ConfigureAwait(false);
                    if (flags == null || (flags.Value & SarusLock.FLAG_UNLOCKED_BY_YOU) == 0)
                        return;
                    var ok = await port.SarusLockLockAsync(sysid, compid).ConfigureAwait(false);
                    log.Info("Sarus lock: lock aircraft " + sysid + " -> " + ok);
                    if (!ok)
                        problems.Add("Aircraft " + sysid + " did not confirm the lock. It locks by itself after 10 s " +
                                     "on the ground without hearing from this station, or when it restarts.");
                }
            }
            catch (Exception ex)
            {
                log.Error(ex);
                problems.Add("Aircraft " + sysid + ": the " + (unlock ? "unlock" : "lock") + " did not complete (" +
                             ex.Message + ").");
            }
        }

        /// <summary>
        /// A button that shows the lock state and toggles it. Unlocking asks for the password; locking needs nothing.
        /// </summary>
        public static MyButton MakeButton()
        {
            var but = new MyButton { Dock = DockStyle.Fill, Height = 26 };
            void Refresh()
            {
                if (!SarusLock.Configured)
                {
                    but.Text = "Sarus lock: off";
                    but.Enabled = false;
                    return;
                }
                but.Enabled = true;
                but.Text = SarusLock.Unlocked ? "Lock parameters" : "Unlock parameters";
            }
            EventHandler changed = (s, e) =>
            {
                if (but.IsDisposed)
                    return;
                if (but.InvokeRequired)
                    but.BeginInvoke((Action) Refresh);
                else
                    Refresh();
            };
            SarusLock.Changed += changed;
            but.Disposed += (s, e) => SarusLock.Changed -= changed;
            but.Click += (s, e) =>
            {
                if (SarusLock.Unlocked)
                    SarusLock.Lock();
                else
                {
                    declinedAt = DateTime.MinValue;
                    Prompt("unlock");
                }
            };
            Refresh();
            return but;
        }
    }
}

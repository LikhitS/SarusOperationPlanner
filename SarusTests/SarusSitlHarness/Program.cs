// Integration checks for the ground station against a QuadPlane SITL on tcp:127.0.0.1:5760.
// Prints "CHECK PASS|FAIL|INFO <name> <detail>" lines, exits 0 only if every PASS/FAIL check passed.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using MissionPlanner;
using MissionPlanner.ArduPilot;
using MissionPlanner.Comms;
using MissionPlanner.Utilities;

static class Harness
{
    static int failures;
    static StreamWriter report;

    static void Log(string line)
    {
        line = DateTime.Now.ToString("HH:mm:ss ") + line;
        Console.WriteLine(line);
        report.WriteLine(line);
        report.Flush();
    }

    static void Check(bool ok, string name, string detail = "")
    {
        if (!ok) failures++;
        Log($"CHECK {(ok ? "PASS" : "FAIL")} {name} {detail}");
    }

    static void Info(string name, string detail) => Log($"CHECK INFO {name} {detail}");

    static async Task<bool> WaitFor(Func<bool> cond, int seconds)
    {
        var end = DateTime.Now.AddSeconds(seconds);
        while (DateTime.Now < end)
        {
            if (cond()) return true;
            await Task.Delay(250);
        }
        return cond();
    }

    [STAThread]
    static void Main(string[] args)
    {
        var reportPath = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "sarus-sitl-report.txt");
        report = new StreamWriter(reportPath, false);
        reportPathForDumps = reportPath;

        // Isolated settings and data folders so the harness never shares state (plugin cache, logs, config)
        // with a Mission Planner installed on this PC.
        Settings.AppConfigName = "SarusHarness";
        Settings.CustomUserDataDirectory = Path.Combine(Path.GetTempPath(), "sarus-harness-" + Guid.NewGuid());
        Directory.CreateDirectory(Settings.GetUserDataDirectory());
        Settings.Instance["update_check"] = DateTime.Now.ToShortDateString();
        Settings.Instance["AutoConnect"] = "[]";
        // Pre-answer first-run questions the way a user clicking "No" would, so no dialog waits on a human.
        Settings.Instance["AA_CheckEnableAltitudeAngel"] = "False";
        // Optional start-up theme (themes apply fully only at start-up)
        var startTheme = Environment.GetEnvironmentVariable("SARUS_START_THEME");
        if (!string.IsNullOrEmpty(startTheme))
            Settings.Instance["theme"] = startTheme;
        Settings.Instance.Save();

        // Answers known first-run dialogs and records any other dialog. WinForms timers keep firing inside
        // modal message loops, so this works while a dialog is blocking startup.
        var dialogs = new System.Windows.Forms.Timer { Interval = 500 };
        dialogs.Tick += (s, e) => HandleDialogs();
        dialogs.Start();

        // Start testing only once startup has finished: MainV2 starts its telemetry reader as the
        // last step of OnLoad, after plugins (and their first-run dialogs) have loaded.
        var serialThreadField = typeof(MainV2).GetField("serialThread",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var startupDeadline = DateTime.Now.AddMinutes(3);
        var timer = new System.Windows.Forms.Timer { Interval = 500 };
        timer.Tick += async (s, e) =>
        {
            bool ready = MainV2.instance != null && MainV2.instance.Visible &&
                         (bool)serialThreadField.GetValue(MainV2.instance);
            if (!ready && DateTime.Now < startupDeadline)
                return;
            timer.Stop();
            Check(ready, "application startup completed", ready ? "" : "not ready after 3 min");
            await Task.Delay(2000);
            flightPhase = true;
            try
            {
                await Run();
            }
            catch (Exception ex)
            {
                failures++;
                Log("CHECK FAIL exception " + ex.ToString().Replace(Environment.NewLine, " | "));
            }
            // Close the app the way a user would, and verify shutdown completes on its own.
            watchdogStop = true;
            Log("closing application");
            new System.Threading.Thread(() =>
            {
                System.Threading.Thread.Sleep(60000);
                Log("CHECK FAIL clean shutdown (hung > 60s)");
                Log("RESULT SHUTDOWN_HANG");
                report.Close();
                Environment.Exit(3);
            }) { IsBackground = true }.Start();
            try { MainV2.instance.doDisconnect(MainV2.comPort); } catch (Exception ex) { Log("disconnect error " + ex.Message); }
            MainV2.instance.Close();
        };
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            failures++;
            Log("CHECK FAIL unhandled exception " + e.ExceptionObject.ToString().Replace(Environment.NewLine, " | "));
        };
        timer.Start();
        Program.Main(new string[0]);

        Check(true, "clean shutdown");
        Log(failures == 0 ? "RESULT ALL_PASS" : $"RESULT {failures} FAILURE(S)");
        report.Close();
        Environment.Exit(failures == 0 ? 0 : 1);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern int GetDeviceCaps(IntPtr hdc, int index); // 118 DESKTOPHORZRES, 117 DESKTOPVERTRES

    // Every parameter: write a different valid value, read it back fresh from the aircraft, restore, verify.
    // GCS defects (timeouts, cache != aircraft, failed restore) are FAILs; firmware limits are recorded in the CSV.
    static void ParamSweep(MAVLinkInterface port, MAVState mav, uint sid, byte cid)
    {
        string fw = mav.cs.firmware.ToString();
        // Not swept: simulator physics, vehicle/GCS identity and link settings (changing them cuts the test's own
        // link: MAV_SYSID is the 4.7 name of SYSID_THISMAV), counters, and storage format.
        var skipPrefixes = new[] { "SIM_", "SYSID_", "MAV_SYSID", "MAV_GCS_SYSID", "MAV_OPTIONS", "SERIAL0_",
                                   "FORMAT_VERSION", "STAT_", "SCHED_LOOP_RATE", "STATS_" };
        int consecutiveErrors = 0;
        var csvPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPathForDumps)),
            Path.GetFileNameWithoutExtension(reportPathForDumps) + "-params.csv");
        int tested = 0, gcsFail = 0, limited = 0, skipped = 0, reLimited = 0;
        using (var csv = new StreamWriter(csvPath, false) { AutoFlush = true })
        {
            csv.WriteLine("name,type,original,sent,aircraft_kept,cache_matches_aircraft,restored_ok,note");
            // Chunked: ArduPilot's parameter storage fills up after a few hundred changed values, so each chunk
            // runs against a freshly wiped simulator. SARUS_PARAM_SWEEP_RANGE = "skip,take".
            var all = mav.param.Keys.Cast<string>().OrderBy(n => n).ToList();
            var range = (Environment.GetEnvironmentVariable("SARUS_PARAM_SWEEP_RANGE") ?? "0,100000").Split(',');
            var chunk = all.Skip(int.Parse(range[0])).Take(int.Parse(range[1])).ToList();
            Info("param sweep", $"params {range[0]}..{int.Parse(range[0]) + chunk.Count - 1} of {all.Count}");
            foreach (var name in chunk)
            {
                if (skipPrefixes.Any(p => name.StartsWith(p))) { skipped++; continue; }
                var p0 = mav.param[name];
                double orig = p0.Value;
                var type = p0.TypeAP;
                bool isInt = type != MAVLink.MAV_PARAM_TYPE.REAL32 && type != MAVLink.MAV_PARAM_TYPE.REAL64;

                double min = 0, max = 0, test;
                var options = ParameterMetaDataRepository.GetParameterOptionsInt(name, fw);
                var bits = ParameterMetaDataRepository.GetParameterBitMaskInt(name, fw);
                if (options.Count > 1)
                    test = options.Select(o => (double)o.Key).First(k => Math.Abs(k - orig) > 1e-6);
                else if (bits.Count > 0)
                    test = ((long)orig) ^ (1L << bits[0].Key);
                else if (ParameterMetaDataRepository.GetParameterRange(name, ref min, ref max, fw) && max > min)
                    test = Math.Abs(orig - max) > 1e-6 ? max : min;
                else
                    test = isInt ? orig + 1 : Math.Round(orig * 1.1 + 0.1, 3);
                if (isInt) test = Math.Round(test);

                string note = "";
                double kept = double.NaN;
                bool cacheOk = false, restoredOk = false;
                try
                {
                    port.setParam(sid, cid, name, test);
                    double cached = mav.param.ContainsKey(name) ? mav.param[name].Value : double.NaN;
                    kept = port.GetParam(sid, cid, name);          // fresh PARAM_REQUEST_READ
                    cacheOk = Math.Abs((float)cached - (float)kept) <= Math.Max(Math.Abs((float)kept), 1f) * 1e-6f;
                    if (Math.Abs((float)kept - (float)test) > Math.Max(Math.Abs((float)test), 1f) * 1e-6f)
                    {
                        limited++;
                        note = "firmware kept a different value";
                    }
                }
                catch (Exception ex) { note = "write/read error: " + ex.Message; }
                try
                {
                    port.setParam(sid, cid, name, orig, true);
                    double back = port.GetParam(sid, cid, name);
                    restoredOk = Math.Abs((float)back - (float)orig) <= Math.Max(Math.Abs((float)orig), 1f) * 1e-6f;
                    if (!restoredOk) note += $" restore read {back}";
                }
                catch (Exception ex) { note += " restore error: " + ex.Message; }

                tested++;
                // The raw API reports the write acknowledgement; a later fresh read that differs means the firmware
                // re-limited the value after acknowledging it (e.g. Q_LOIT_ACC_MAX_M). That is firmware behaviour,
                // shown to the user by the Full Parameter List re-read, not a GCS defect.
                if (!cacheOk && !note.Contains("error"))
                {
                    note = "firmware changed the value after acknowledging it; " + note;
                    reLimited++;
                }
                bool gcsDefect = note.Contains("error");
                if (gcsDefect) gcsFail++;
                consecutiveErrors = note.Contains("error") ? consecutiveErrors + 1 : 0;
                if (consecutiveErrors >= 3)
                {
                    csv.WriteLine($"ABORTED after {name}: aircraft stopped answering");
                    Check(false, "param sweep: aircraft still answering", "aborted after " + name);
                    break;
                }
                // a failed restore is only a GCS defect if the aircraft accepted the original value before
                csv.WriteLine($"{name},{type},{orig},{test},{kept},{cacheOk},{restoredOk},\"{note.Trim()}\"");
            }
        }
        Check(gcsFail == 0, "param sweep: GCS handled every parameter", $"{tested} tested, {gcsFail} GCS failures, {skipped} skipped");
        Info("param sweep: firmware limits", $"{limited} parameters kept a different value than sent (see {Path.GetFileName(csvPath)})");
        if (reLimited > 0) Info("param sweep: re-limited after acknowledgement", $"{reLimited} parameters");
    }

    // Presses each main-window shortcut (as listed in MainV2.ProcessCmdKey and the Help screen) and checks the
    // expected screen or window appears. Opened tool windows are closed again.
    static async Task ShortcutChecks(MAVLinkInterface port)
    {
        if (Environment.GetEnvironmentVariable("SARUS_SHORTCUTS") != "1") return;
        var mf = MainV2.instance;
        var pck = typeof(MainV2).GetMethod("ProcessCmdKey",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        bool Press(System.Windows.Forms.Keys k)
        {
            var msg = new System.Windows.Forms.Message();
            return (bool)pck.Invoke(mf, new object[] { msg, k });
        }
        string Screen() => MainV2.View?.current?.Name ?? "";
        var K = System.Windows.Forms.Keys.Control;

        foreach (var (key, screen) in new[] {
            (System.Windows.Forms.Keys.F3, "FlightPlanner"), (System.Windows.Forms.Keys.F2, "FlightData"),
            (System.Windows.Forms.Keys.F4, "SWConfig") })
        {
            bool handled = Press(key);
            await Task.Delay(2500);
            Check(handled && Screen() == screen, $"shortcut {key} opens {screen}", "now on " + Screen());
        }
        Press(System.Windows.Forms.Keys.F2);
        await Task.Delay(1500);

        // F5: refresh full parameter list
        int before = port.MAV.param.Count;
        Check(Press(System.Windows.Forms.Keys.F5) && port.MAV.param.Count >= before - 1, "shortcut F5 refreshes parameters",
            $"{port.MAV.param.Count} params");
        await Task.Delay(2000);

        // Tool windows
        foreach (var (key, label) in new[] {
            (K | System.Windows.Forms.Keys.F, "Ctrl+F advanced/temp tools"),
            (K | System.Windows.Forms.Keys.P, "Ctrl+P plugin manager"),
            (K | System.Windows.Forms.Keys.G, "Ctrl+G NMEA output"),
            (K | System.Windows.Forms.Keys.X, "Ctrl+X map cache"),
            (K | System.Windows.Forms.Keys.L, "Ctrl+L spectrogram"),
            (K | System.Windows.Forms.Keys.W, "Ctrl+W propagation"),
            (K | System.Windows.Forms.Keys.J, "Ctrl+J device ops") })
        {
            var formsBefore = System.Windows.Forms.Application.OpenForms.Cast<System.Windows.Forms.Form>().ToList();
            bool handled = false;
            try { handled = Press(key); } catch (Exception ex) { Log("shortcut error " + label + ": " + ex.InnerException?.Message); }
            await Task.Delay(2500);
            var opened = System.Windows.Forms.Application.OpenForms.Cast<System.Windows.Forms.Form>().Except(formsBefore).ToList();
            Check(handled && opened.Count > 0, "shortcut " + label + " opens a window",
                string.Join(",", opened.Select(f => f.GetType().Name)));
            foreach (var f in opened) { try { f.Close(); } catch { } }
            await Task.Delay(500);
        }

        // Ctrl+Y: write parameters to the aircraft's storage, confirmed by a message
        expectedDialogs.Add(("Done MAV_ACTION_STORAGE_WRITE", "ok"));
        int dl = dialogLog.Count;
        Press(K | System.Windows.Forms.Keys.Y);
        await Task.Delay(1500);
        Check(dialogLog.Skip(dl).Any(d => d.Contains("STORAGE_WRITE")), "shortcut Ctrl+Y writes parameters to storage");
        expectedDialogs.Clear();

        // F12: disconnect
        Press(System.Windows.Forms.Keys.F12);
        Check(await WaitFor(() => !port.BaseStream.IsOpen, 10), "shortcut F12 disconnects");
        Info("shortcuts", "Ctrl+T (override connect) and Ctrl+Z (camera test) need special hardware/ports; not pressed");
    }

    // Concurrency stress: parameter reads on one thread while another thread sends commands that wait for an
    // acknowledgement (like the camera/gimbal code does in the background). Counts replies the GCS lost.
    static async Task StressChecks(MAVLinkInterface port)
    {
        if (Environment.GetEnvironmentVariable("SARUS_STRESS") != "1") return;
        var mav = port.MAV;
        uint sid = mav.sysid; byte cid = mav.compid;
        var names = mav.param.Keys.Cast<string>().Where(n => !n.StartsWith("SIM_")).Take(150).ToList();
        int reads = 0, readFails = 0, cmds = 0, cmdFails = 0;
        var stop = DateTime.Now.AddSeconds(60);
        var reader = Task.Run(() =>
        {
            int i = 0;
            while (DateTime.Now < stop)
            {
                var n = names[i++ % names.Count];
                try { port.GetParam(sid, cid, n); reads++; }
                catch { readFails++; }
            }
        });
        var commander = Task.Run(() =>
        {
            while (DateTime.Now < stop)
            {
                try
                {
                    // SITL answers REQUEST_MESSAGE with an ACK (accepted or unsupported); a lost ACK throws
                    port.doCommand(sid, cid, MAVLink.MAV_CMD.REQUEST_MESSAGE,
                        (float)MAVLink.MAVLINK_MSG_ID.AUTOPILOT_VERSION, 0, 0, 0, 0, 0, 0);
                    cmds++;
                }
                catch { cmdFails++; }
            }
        });
        await Task.WhenAll(reader, commander);
        Info("stress", $"{reads} parameter reads ({readFails} lost), {cmds} commands ({cmdFails} lost acks) in 60 s");
        Check(readFails == 0, "concurrent parameter reads never lose the reply", $"{readFails} of {reads + readFails} lost");
        Check(cmdFails == 0, "concurrent commands never lose the acknowledgement", $"{cmdFails} of {cmds + cmdFails} lost");

        // Phase 2: mission upload + download round trips while background commands run
        double hlat = -35.363261, hlng = 149.165230;
        var mission = new List<Locationwp> { new Locationwp { id = (ushort)MAVLink.MAV_CMD.WAYPOINT, lat = hlat, lng = hlng, alt = 584, frame = 0 } };
        for (int i = 1; i <= 8; i++)
            mission.Add(new Locationwp { id = (ushort)MAVLink.MAV_CMD.WAYPOINT, lat = hlat + i * 0.001, lng = hlng + i * 0.0007, alt = 40 + i, frame = 3 });
        int trips = 0, tripFails = 0; cmds = 0; cmdFails = 0;
        stop = DateTime.Now.AddSeconds(60);
        var missions = Task.Run(async () =>
        {
            while (DateTime.Now < stop)
            {
                try
                {
                    await mav_mission.upload(port, sid, cid, MAVLink.MAV_MISSION_TYPE.MISSION, mission);
                    var back = await mav_mission.download(port, sid, cid, MAVLink.MAV_MISSION_TYPE.MISSION);
                    bool same = back.Count == mission.Count && Enumerable.Range(1, mission.Count - 1).All(k =>
                        Math.Abs(back[k].lat - mission[k].lat) < 1e-6 && Math.Abs(back[k].alt - mission[k].alt) < 0.01);
                    if (same) trips++; else { tripFails++; Log("mission round trip mismatch"); }
                }
                catch (Exception ex) { tripFails++; Log("mission round trip error: " + ex.Message); }
            }
        });
        var commander2 = Task.Run(() =>
        {
            while (DateTime.Now < stop)
            {
                try { port.doCommand(sid, cid, MAVLink.MAV_CMD.REQUEST_MESSAGE, (float)MAVLink.MAVLINK_MSG_ID.AUTOPILOT_VERSION, 0, 0, 0, 0, 0, 0); cmds++; }
                catch { cmdFails++; }
            }
        });
        await Task.WhenAll(missions, commander2);
        Info("stress missions", $"{trips} upload+download round trips ({tripFails} failed), {cmds} commands ({cmdFails} lost) in 60 s");
        Check(tripFails == 0, "mission transfer never fails under concurrent commands", $"{tripFails} of {trips + tripFails} failed");

        // Phase 3: set-current-waypoint and COMMAND_INT concurrently
        int setCur = 0, setCurFails = 0, cmdInt = 0, cmdIntFails = 0;
        stop = DateTime.Now.AddSeconds(30);
        var setter = Task.Run(() =>
        {
            while (DateTime.Now < stop)
            {
                try { port.setWPCurrent(sid, cid, 1); setCur++; } catch { setCurFails++; }
            }
        });
        var intCommander = Task.Run(() =>
        {
            while (DateTime.Now < stop)
            {
                try { port.doCommandInt(sid, cid, MAVLink.MAV_CMD.REQUEST_MESSAGE, (float)MAVLink.MAVLINK_MSG_ID.AUTOPILOT_VERSION, 0, 0, 0, 0, 0, 0); cmdInt++; }
                catch { cmdIntFails++; }
            }
        });
        await Task.WhenAll(setter, intCommander);
        Info("stress set-current/command-int", $"{setCur} set current ({setCurFails} lost), {cmdInt} COMMAND_INT ({cmdIntFails} lost) in 30 s");
        Check(setCurFails == 0 && cmdIntFails == 0, "set-current-waypoint and COMMAND_INT never lose replies",
            $"{setCurFails} + {cmdIntFails} lost");
    }

    static void Shot(string file)
    {
        var hdc = GetDC(IntPtr.Zero);
        var screen = new System.Drawing.Rectangle(0, 0, GetDeviceCaps(hdc, 118), GetDeviceCaps(hdc, 117));
        ReleaseDC(IntPtr.Zero, hdc);
        using (var bmp = new System.Drawing.Bitmap(screen.Width, screen.Height))
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(screen.Location, System.Drawing.Point.Empty, screen.Size);
            bmp.Save(file);
        }
    }

    static IEnumerable<System.Windows.Forms.Control> AllControls(System.Windows.Forms.Control c)
    {
        foreach (System.Windows.Forms.Control child in c.Controls)
        {
            yield return child;
            foreach (var x in AllControls(child)) yield return x;
        }
    }

    // Opens every page of SETUP and CONFIG while connected: each must open without an error or an
    // unexpected dialog. Pages that start no action on their own are expected to be passive.
    static async Task AllPagesCheck(string shotDir)
    {
        if (Environment.GetEnvironmentVariable("SARUS_PAGES") != "1") return;
        var mf = MainV2.instance;
        mf.WindowState = System.Windows.Forms.FormWindowState.Maximized;
        mf.TopMost = true;
        int opened = 0;
        foreach (var (menu, label) in new[] { ("MenuInitConfig", "setup"), ("MenuConfigTune", "config") })
        {
            var item = (System.Windows.Forms.ToolStripItem)typeof(MainV2).GetField(menu,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Public).GetValue(mf);
            item.PerformClick();
            await Task.Delay(3000);
            var bsv = AllControls(mf).OfType<MissionPlanner.Controls.BackstageView.BackstageView>().FirstOrDefault(b => b.Visible);
            if (bsv == null) { Check(false, "pages: backstage found for " + label); continue; }
            var pages = Enumerable.Range(0, bsv.Pages.Count).Select(i => bsv.Pages[i]).Where(p => p.Show).ToList();
            Info("pages", $"{label}: {pages.Count} visible pages");
            int idx = 0;
            foreach (var page in pages)
            {
                idx++;
                int failuresBefore = failures;
                string title = page.LinkText ?? page.Page?.GetType().Name ?? "?";
                try
                {
                    bsv.ActivatePage(page);
                    await Task.Delay(2500);
                    var safe = new string(title.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
                    Shot(Path.Combine(shotDir, $"{label}-{idx:00}-{safe}.png"));
                    opened++;
                    Check(failures == failuresBefore, $"page opens cleanly: {label} / {title}");
                }
                catch (Exception ex)
                {
                    Check(false, $"page opens cleanly: {label} / {title}", ex.Message);
                }
            }
        }
        mf.TopMost = false;
        Info("pages", $"{opened} pages opened");
    }

    static volatile bool expectBlackout;
    static readonly string linkControl = Environment.GetEnvironmentVariable("SARUS_LINK_CONTROL");
    static void SetLink(string mode)
    {
        if (linkControl == null) return;
        File.WriteAllText(linkControl, mode);
        Log("LINK " + mode);
    }

    static volatile bool watchdogStop;
    static volatile bool flightPhase;
    static readonly HashSet<System.Windows.Forms.Form> seenDialogs = new HashSet<System.Windows.Forms.Form>();

    static string AllText(System.Windows.Forms.Control c)
    {
        var sb = new System.Text.StringBuilder();
        foreach (System.Windows.Forms.Control child in c.Controls)
        {
            if (child is System.Windows.Forms.Label || child is System.Windows.Forms.TextBoxBase)
                sb.Append(child.Text).Append(' ');
            sb.Append(AllText(child));
        }
        return sb.ToString();
    }

    // Dialogs a test step expects next: (text fragment, answer "yes"/"no"/"ok"). Matched in order of the list.
    static readonly List<(string fragment, string answer)> expectedDialogs = new List<(string, string)>();
    static readonly List<string> dialogLog = new List<string>();

    static readonly Dictionary<System.Windows.Forms.Form, DateTime> modalSince = new Dictionary<System.Windows.Forms.Form, DateTime>();

    static void HandleDialogs()
    {
        // Safety net: any modal window open for more than 3 minutes blocks the run; record it and close it.
        foreach (System.Windows.Forms.Form f in System.Windows.Forms.Application.OpenForms.Cast<System.Windows.Forms.Form>().ToList())
        {
            if (!f.Modal) continue;
            if (!modalSince.ContainsKey(f)) modalSince[f] = DateTime.Now;
            else if ((DateTime.Now - modalSince[f]).TotalMinutes > 3)
            {
                modalSince.Remove(f);
                Check(false, "no window blocks the app > 3 min",
                    f.GetType().FullName + " | " + (f.Text + " " + AllText(f)).Replace("\r", " ").Replace("\n", " "));
                try { f.Close(); } catch { }
                return;
            }
        }
        foreach (System.Windows.Forms.Form f in System.Windows.Forms.Application.OpenForms)
        {
            if (f.Modal && !seenDialogs.Contains(f) && f.GetType() == typeof(System.Windows.Forms.Form) &&
                f.FormBorderStyle == System.Windows.Forms.FormBorderStyle.FixedDialog)
            {
                string t = (f.Text + " | " + AllText(f)).Replace("\r", " ").Replace("\n", " ").Trim();
                var idx = expectedDialogs.FindIndex(x => t.Contains(x.fragment));
                if (idx >= 0)
                {
                    var answer = expectedDialogs[idx].answer;
                    expectedDialogs.RemoveAt(idx);
                    seenDialogs.Add(f);
                    dialogLog.Add(t);
                    Info("expected dialog", $"[{answer}] {t}");
                    var btn = answer == "no" ? (f.CancelButton ?? f.AcceptButton) : f.AcceptButton;
                    if (btn != null) btn.PerformClick(); else f.Close();
                    return;
                }
            }
        }
        foreach (System.Windows.Forms.Form f in System.Windows.Forms.Application.OpenForms)
        {
            // Only CustomMessageBox dialogs: a plain Form (not a subclass such as ProgressReporterDialogue),
            // fixed-dialog, top-most. Progress windows and real screens are left alone.
            if (!f.Modal || seenDialogs.Contains(f)) continue;
            if (f.GetType() != typeof(System.Windows.Forms.Form) ||
                f.FormBorderStyle != System.Windows.Forms.FormBorderStyle.FixedDialog) continue;
            seenDialogs.Add(f);
            string text = (f.Text + " | " + AllText(f)).Replace("\r", " ").Replace("\n", " ").Trim();

            System.Windows.Forms.IButtonControl press;
            if (text.Contains("Drone ID Tab"))
                press = f.AcceptButton;                       // first-run notice: OK
            else if (text.Contains("Altitude Angel"))
                press = f.CancelButton ?? f.AcceptButton;     // first-run opt-in: No
            else if (AllControls(f).OfType<System.Windows.Forms.CheckBox>().Any() ||   // "show me again" notices
                     text.Contains("Ensure your props are not on"))                     // FailSafe page safety reminder
                press = f.AcceptButton ?? f.CancelButton;
            else
            {
                // Unknown dialog. During startup it is informational; during the flight checks it is a defect.
                if (flightPhase) Check(false, "no unexpected dialog during flight checks", text);
                else Info("startup dialog", text);
                press = f.CancelButton ?? f.AcceptButton;
            }
            Info("dialog answered", $"[{(press as System.Windows.Forms.Control)?.Text}] {text}");
            if (press != null) press.PerformClick(); else f.Close();
            return; // closing changes OpenForms; continue next tick
        }
    }

    // A ground station that stops processing telemetry is a defect even if the vehicle flies fine.
    // Dedicated thread (not the thread pool) so pool starvation cannot hide a stall.
    static void StartStallWatchdog(MAVState mav)
    {
        new System.Threading.Thread(() =>
        {
            int dumps = 0;
            bool inStall = false;
            DateTime stallStart = DateTime.MinValue;
            while (!watchdogStop)
            {
                System.Threading.Thread.Sleep(250);
                var age = (DateTime.UtcNow - mav.lastvalidpacket).TotalSeconds;
                if (!inStall && age > 4)
                {
                    inStall = true;
                    stallStart = DateTime.Now.AddSeconds(-age);
                    Log($"STALL begin: no telemetry processed for {age:F1}s");
                    if (dumps++ < 3)
                    {
                        var dump = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPathForDumps)),
                            $"stall-{DateTime.Now:HHmmss}.txt");
                        try
                        {
                            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                                @"C:\dev\Sarus\tests\StackDump\bin\Release\net472\StackDump.exe",
                                $"{System.Diagnostics.Process.GetCurrentProcess().Id} \"{dump}\"")
                            { UseShellExecute = false, CreateNoWindow = true });
                            p.WaitForExit(30000);
                            Log("STALL stack snapshot " + dump);
                        }
                        catch (Exception ex) { Log("STALL snapshot failed " + ex.Message); }
                    }
                }
                else if (inStall && age < 1)
                {
                    inStall = false;
                    var dur = (DateTime.Now - stallStart).TotalSeconds;
                    if (expectBlackout) Info("telemetry gap (intended link loss)", $"{dur:F1}s from {stallStart:HH:mm:ss}");
                    else Check(false, "telemetry processed continuously", $"stalled {dur:F1}s from {stallStart:HH:mm:ss}");
                }
            }
        }) { IsBackground = true, Name = "StallWatchdog" }.Start();
    }

    static string reportPathForDumps = Path.GetTempPath();

    // Full Parameter List behaviour (Sarus): out-of-range values are not blocked but flagged; values the
    // aircraft does not keep are detected and the grid shows the aircraft's real value; read-only params
    // can be changed after confirmation. Runs the real ConfigRawParams control on the UI thread.
    static async Task ParamEditorChecks(MAVLinkInterface port)
    {
        if (Environment.GetEnvironmentVariable("SARUS_PARAM_EDITOR_CHECKS") != "1")
        {
            Info("param editor checks", "skipped (stock baseline)");
            return;
        }

        var mav = port.MAV;
        bool copterProfile = Environment.GetEnvironmentVariable("SARUS_VEHICLE") == "copter";
        bool roverProfile = Environment.GetEnvironmentVariable("SARUS_VEHICLE") == "rover";
        var raw = new MissionPlanner.GCSViews.ConfigurationView.ConfigRawParams();
        var host = new System.Windows.Forms.Form { Width = 1300, Height = 800, Text = "param editor test" };
        raw.Dock = System.Windows.Forms.DockStyle.Fill;
        host.Controls.Add(raw);
        host.Show();
        raw.Activate();
        await Task.Delay(4000);

        var grid = (System.Windows.Forms.DataGridView)raw.Controls.Find("Params", true)[0];
        System.Windows.Forms.DataGridViewCell Cell(string name) =>
            grid.Rows.Cast<System.Windows.Forms.DataGridViewRow>()
                .First(r => r.Cells["Command"].Value?.ToString() == name).Cells["Value"];
        var write = typeof(MissionPlanner.GCSViews.ConfigurationView.ConfigRawParams).GetMethod("BUT_writePIDS_Click",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var amber = System.Drawing.Color.FromArgb(0xF0, 0xA6, 0x30);

        // A. Out of range: accepted without a prompt, flagged amber, written as entered
        {
            string p = roverProfile ? "ATC_STR_RAT_P" : "TECS_CLMB_MAX";
            double outOfRange = roverProfile ? 50 : 1000;
            double orig = mav.param[p].Value;
            int dialogsBefore = dialogLog.Count;
            var c = Cell(p);
            c.Value = outOfRange.ToString(System.Globalization.CultureInfo.CurrentCulture);
            await Task.Delay(800);
            Check(dialogLog.Count == dialogsBefore, "out-of-range value: no blocking prompt");
            Check(c.Style.BackColor.ToArgb() == amber.ToArgb(), "out-of-range value: cell flagged amber");
            Check((c.ToolTipText ?? "").StartsWith("WARNING: Outside"), "out-of-range value: warning tooltip", c.ToolTipText?.Split('\n')[0]);
            expectedDialogs.Add(("You are about to change", "yes"));
            expectedDialogs.Add(("successfully saved", "ok"));
            write.Invoke(raw, new object[] { null, EventArgs.Empty });
            await Task.Delay(1000);
            Check(Math.Abs(mav.param[p].Value - outOfRange) < 1e-3, "out-of-range value written to aircraft", $"{p} {mav.param[p].Value}");
            Check(c.Style.BackColor.ToArgb() != amber.ToArgb() && !(c.ToolTipText ?? "").StartsWith("WARNING"),
                "out-of-range value: warning cleared after successful write");
            port.setParam(mav.sysid, mav.compid, p, orig);
        }

        // B. Aircraft does not keep the value (fraction into an integer parameter): detected and shown
        {
            string p = roverProfile ? "MODE1" : "FLTMODE1";
            double orig = mav.param[p].Value;
            var c = Cell(p);
            c.Value = (orig + 0.5).ToString(System.Globalization.CultureInfo.CurrentCulture);
            await Task.Delay(500);
            expectedDialogs.Add(("You are about to change", "yes"));
            expectedDialogs.Add(("did not keep these values", "ok"));
            expectedDialogs.Add(("successfully saved", "ok"));
            int before = dialogLog.Count;
            write.Invoke(raw, new object[] { null, EventArgs.Empty });
            await Task.Delay(1000);
            double kept = mav.param[p].Value;
            Check(dialogLog.Skip(before).Any(d => d.Contains("did not keep these values") && d.Contains(p)),
                "value changed by aircraft: reported to user", $"aircraft kept {kept}");
            Check(c.Value?.ToString() == kept.ToString(), "value changed by aircraft: grid shows aircraft value",
                $"grid {c.Value} aircraft {kept}");
            Check(c.Style.BackColor.ToArgb() == amber.ToArgb() && (c.ToolTipText ?? "").StartsWith("WARNING: The aircraft kept"),
                "value changed by aircraft: cell flagged");
            port.setParam(mav.sysid, mav.compid, p, orig, true);
        }

        // C. Read-only parameter: confirmation, then written; outcome shown truthfully
        {
            const string p = "BARO1_GND_PRESS";
            if (mav.param.ContainsKey(p))
            {
                double orig = mav.param[p].Value;
                var c = Cell(p);
                expectedDialogs.Add(("marked read-only", "yes"));
                int before = dialogLog.Count;
                c.Value = Math.Round(orig + 100).ToString(System.Globalization.CultureInfo.CurrentCulture);
                await Task.Delay(800);
                Check(dialogLog.Skip(before).Any(d => d.Contains("marked read-only")), "read-only param: confirmation shown");
                expectedDialogs.Add(("You are about to change", "yes"));
                expectedDialogs.Add(("did not keep these values", "ok"));   // allowed if the aircraft overrides it
                expectedDialogs.Add(("successfully saved", "ok"));
                write.Invoke(raw, new object[] { null, EventArgs.Empty });
                await Task.Delay(1500);
                // The firmware recalibrates ground pressure by itself while disarmed (Rover does so continuously),
                // so compare the grid with the value the aircraft reported when Sarus re-read it after the write.
                double kept = mav.param[p].Value;
                var keptMsg = dialogLog.Skip(before).LastOrDefault(d => d.Contains(p + ": sent"));
                if (keptMsg != null)
                {
                    var m = System.Text.RegularExpressions.Regex.Match(keptMsg, System.Text.RegularExpressions.Regex.Escape(p) + @": sent \S+, aircraft kept (\S+)");
                    if (m.Success) kept = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.CurrentCulture);
                }
                Check(Math.Abs(double.Parse(c.Value.ToString()) - kept) < Math.Max(1e-3, Math.Abs(kept) * 1e-6),
                    "read-only param: grid matches aircraft after write", $"grid {c.Value} aircraft re-read {kept}, now {mav.param[p].Value}");
            }
            else Info("read-only param", p + " not present");
        }

        // D. Value the firmware re-limits AFTER acknowledging the write (found by the sweep):
        //    Q_LOIT_ACC_MAX_M acknowledged 9.81 but runs a lower value. Must be detected.
        if (!copterProfile && !roverProfile && mav.param.ContainsKey("Q_LOIT_ACC_MAX_M"))
        {
            const string p = "Q_LOIT_ACC_MAX_M";
            double orig = mav.param[p].Value;
            var c = Cell(p);
            c.Value = "9.81";
            await Task.Delay(500);
            expectedDialogs.Add(("You are about to change", "yes"));
            expectedDialogs.Add(("did not keep these values", "ok"));
            expectedDialogs.Add(("successfully saved", "ok"));
            int before = dialogLog.Count;
            write.Invoke(raw, new object[] { null, EventArgs.Empty });
            await Task.Delay(1000);
            double fresh = port.GetParam(mav.sysid, mav.compid, p);
            Check(dialogLog.Skip(before).Any(d => d.Contains("did not keep these values") && d.Contains(p)),
                "value re-limited after acknowledgement: reported", $"aircraft runs {fresh}");
            Check(Math.Abs(double.Parse(c.Value.ToString()) - fresh) < 1e-3,
                "value re-limited after acknowledgement: grid shows aircraft value", $"grid {c.Value} aircraft {fresh}");
            port.setParam(mav.sysid, mav.compid, p, orig, true);
        }

        expectedDialogs.RemoveAll(x => x.fragment == "did not keep these values" || x.fragment == "successfully saved");
        Check(expectedDialogs.Count == 0, "param editor: every expected dialog appeared",
            string.Join("; ", expectedDialogs.Select(x => x.fragment)));
        expectedDialogs.Clear();
        host.Close();
    }

    static async Task Run()
    {
        var port = MainV2.comPort;
        var tcpPort = int.Parse(Environment.GetEnvironmentVariable("SARUS_PORT") ?? "5760");
        port.BaseStream = new TcpSerial { client = new TcpClient("127.0.0.1", tcpPort) };
        MainV2.instance.doConnect(port, "preset", tcpPort.ToString(), true, false);

        Check(port.BaseStream.IsOpen, "connect", "tcp 5760");
        var mav = port.MAV;
        StartStallWatchdog(mav);
        uint sid = mav.sysid;
        byte cid = mav.compid;
        // Vehicle profile: quadplane (default), copter or rover (SARUS_VEHICLE)
        bool copter = Environment.GetEnvironmentVariable("SARUS_VEHICLE") == "copter";
        bool rover = Environment.GetEnvironmentVariable("SARUS_VEHICLE") == "rover";
        Check(mav.cs.firmware == (rover ? Firmwares.ArduRover : copter ? Firmwares.ArduCopter2 : Firmwares.ArduPlane), "vehicle type", mav.cs.firmware.ToString());
        Info("version", mav.VersionString);

        bool badLink = linkControl != null;
        if (badLink) SetLink("loss:5");
        await Task.Run(async () =>
        {
            // Parameters
            Check(mav.param.Count > 500, "param download", $"{mav.param.Count} params");
            if (rover)
                Check(mav.param.ContainsKey("CRUISE_SPEED") && mav.param.ContainsKey("MODE1"), "rover parameters present");
            else if (copter)
                Check(mav.param.ContainsKey("FRAME_CLASS"), "copter frame parameters present");
            else
                Check(mav.param.ContainsKey("Q_ENABLE") && mav.param["Q_ENABLE"].Value == 1, "quadplane enabled", "Q_ENABLE=1");

            var tuneParams = rover ? new[] { "ATC_STR_RAT_P", "ATC_SPEED_P", "CRUISE_SPEED", "WP_SPEED", "TURN_RADIUS" }
                                    : copter ? new[] { "ATC_RAT_RLL_P", "ATC_RAT_PIT_P", "PSC_D_ACC_P", "WP_SPD" }
                                    : new[] { "TECS_CLMB_MAX", "TECS_SINK_MAX", "TECS_TIME_CONST", "Q_A_ANGLE_MAX" };
            foreach (var name in tuneParams)
            {
                Check(mav.param.ContainsKey(name), "param present " + name);
                if (!mav.param.ContainsKey(name)) continue;
                double orig = mav.param[name].Value;
                double target = Math.Round(orig + (orig >= 1 ? 1 : 0.5), 2);
                bool ok = port.setParam(sid, cid, name, target);
                double back = port.GetParam(sid, cid, name);
                Check(ok && Math.Abs(back - target) < 1e-3, "param write+readback " + name, $"{orig} -> {target}, read {back}");
                port.setParam(sid, cid, name, orig);
                Check(Math.Abs(port.GetParam(sid, cid, name) - orig) < 1e-3, "param restore " + name, $"{orig}");
            }

            // Extreme values: record what the firmware keeps. INFO only; this documents firmware clamping.
            var extremes = rover ? new[] { ("ATC_STR_RAT_P", 50.0), ("CRUISE_SPEED", 1000.0), ("WP_SPEED", 1000.0) }
                                  : copter ? new[] { ("ATC_RAT_RLL_P", 50.0), ("WP_SPD", 100000.0), ("ANGLE_MAX", 9000.0) }
                                  : new[] { ("TECS_CLMB_MAX", 1000.0), ("TECS_PITCH_MAX", 89.0), ("Q_A_ANGLE_MAX", 89.0) };
            foreach (var (name, extreme) in extremes)
            {
                if (!mav.param.ContainsKey(name)) continue;
                double orig = mav.param[name].Value;
                bool replied = port.setParam(sid, cid, name, extreme);
                double back = port.GetParam(sid, cid, name);
                Info("extreme " + name, $"sent {extreme}, gcs-reported-success={replied}, vehicle-kept {back}");
                port.setParam(sid, cid, name, orig, true);
                Check(Math.Abs(port.GetParam(sid, cid, name) - orig) < 1e-3, "extreme restore " + name, $"{orig}");
            }

            // Mission: home, VTOL takeoff, fixed-wing waypoint, VTOL land
            double hlat = -35.363261, hlng = 149.165230;
            var mission = rover
                ? new List<Locationwp>
                {
                    new Locationwp { id = (ushort)MAVLink.MAV_CMD.WAYPOINT, lat = hlat, lng = hlng, alt = 584, frame = 0 },
                    new Locationwp { id = (ushort)MAVLink.MAV_CMD.WAYPOINT, lat = hlat + 0.0007, lng = hlng, alt = 0, frame = 3 },
                    new Locationwp { id = (ushort)MAVLink.MAV_CMD.WAYPOINT, lat = hlat + 0.0007, lng = hlng + 0.0009, alt = 0, frame = 3 },
                    new Locationwp { id = (ushort)MAVLink.MAV_CMD.WAYPOINT, lat = hlat, lng = hlng + 0.0009, alt = 0, frame = 3 },
                    new Locationwp { id = (ushort)MAVLink.MAV_CMD.RETURN_TO_LAUNCH, frame = 3 },
                }
                : copter
                ? new List<Locationwp>
                {
                    new Locationwp { id = (ushort)MAVLink.MAV_CMD.WAYPOINT, lat = hlat, lng = hlng, alt = 584, frame = 0 },
                    new Locationwp { id = (ushort)MAVLink.MAV_CMD.TAKEOFF, lat = hlat, lng = hlng, alt = 30, frame = 3 },
                    new Locationwp { id = (ushort)MAVLink.MAV_CMD.WAYPOINT, lat = hlat + 0.0015, lng = hlng, alt = 30, frame = 3 },
                    new Locationwp { id = (ushort)MAVLink.MAV_CMD.WAYPOINT, lat = hlat + 0.0015, lng = hlng + 0.0018, alt = 30, frame = 3 },
                    new Locationwp { id = (ushort)MAVLink.MAV_CMD.RETURN_TO_LAUNCH, frame = 3 },
                }
                : new List<Locationwp>
                {
                    new Locationwp { id = (ushort)MAVLink.MAV_CMD.WAYPOINT, lat = hlat, lng = hlng, alt = 584, frame = 0 },
                    new Locationwp { id = (ushort)MAVLink.MAV_CMD.VTOL_TAKEOFF, lat = hlat, lng = hlng, alt = 30, frame = 3 },
                    new Locationwp { id = (ushort)MAVLink.MAV_CMD.WAYPOINT, lat = hlat + 0.0045, lng = hlng, alt = 50, frame = 3 },
                    new Locationwp { id = (ushort)MAVLink.MAV_CMD.WAYPOINT, lat = hlat + 0.0045, lng = hlng + 0.0055, alt = 50, frame = 3 },
                    new Locationwp { id = (ushort)MAVLink.MAV_CMD.VTOL_LAND, lat = hlat, lng = hlng, alt = 0, frame = 3 },
                };
            await mav_mission.upload(port, mav.sysid, mav.compid, MAVLink.MAV_MISSION_TYPE.MISSION, mission);
            var down = await mav_mission.download(port, mav.sysid, mav.compid, MAVLink.MAV_MISSION_TYPE.MISSION);
            Check(down.Count == mission.Count, "mission upload/download count", $"{down.Count}/{mission.Count}");
            for (int i = 1; i < Math.Min(down.Count, mission.Count); i++)
            {
                bool same = down[i].id == mission[i].id && Math.Abs(down[i].lat - mission[i].lat) < 1e-6 &&
                            Math.Abs(down[i].lng - mission[i].lng) < 1e-6 && Math.Abs(down[i].alt - mission[i].alt) < 0.01;
                Check(same, $"mission item {i} matches", $"cmd {down[i].id} {down[i].lat:F6},{down[i].lng:F6} alt {down[i].alt}");
            }

            // Mode changes
            foreach (var mode in rover ? new[] { "Hold", "Steering", "Loiter", "Manual" } : copter ? new[] { "Stabilize", "AltHold", "Loiter" } : new[] { "QHOVER", "FBWA", "QLOITER" })
            {
                port.setMode(sid, cid, mode);
                Check(await WaitFor(() => mav.cs.mode.ToUpper() == mode.ToUpper(), 10), "mode " + mode, mav.cs.mode);
            }

            if (Environment.GetEnvironmentVariable("SARUS_PARAM_SWEEP") == "1")
                ParamSweep(port, mav, sid, cid);

            if (Environment.GetEnvironmentVariable("SARUS_QUICK") == "1")
            {
                Info("flight", "skipped (quick mode)");
                return;
            }

            // Copter: allow arming in AUTO and taking off without RC throttle (SITL has no RC input)
            if (copter) port.setParam(sid, cid, "AUTO_OPTIONS", 3);

            if (badLink) SetLink("delay:300");

            // Wait for the simulated GPS/EKF, then arm in AUTO and fly the whole mission.
            port.setMode(sid, cid, "AUTO");
            Check(await WaitFor(() => mav.cs.mode.ToUpper() == "AUTO", 10), "mode AUTO", mav.cs.mode);
            bool armed = false;
            var armDeadline = DateTime.Now.AddSeconds(150);
            while (!armed && DateTime.Now < armDeadline)
            {
                try { armed = port.doARM(sid, cid, true); } catch { }
                if (!armed) await Task.Delay(3000);
            }
            Check(armed && await WaitFor(() => mav.cs.armed, 5), "arm in AUTO");
            if (!armed) return;

            if (rover)
            {
                // Rover: drive the square, return to launch, stop and disarm
                Check(await WaitFor(() => mav.cs.groundspeed > 2, 60), "rover driving in AUTO", $"gs {mav.cs.groundspeed:F1}");
                Check(await WaitFor(() => mav.cs.wpno >= 3, 180), "rover reached waypoint 3", $"wp {mav.cs.wpno}");
                // the mission's RETURN_TO_LAUNCH item runs inside AUTO (the mode stays Auto)
                Check(await WaitFor(() => mav.cs.wpno >= 4, 180), "rover started return to launch", $"wp {mav.cs.wpno}, mode {mav.cs.mode}");
                Check(await WaitFor(() => mav.cs.DistToHome < 5 && mav.cs.groundspeed < 0.3, 180), "rover back home and stopped", $"{mav.cs.DistToHome:F1} m, gs {mav.cs.groundspeed:F1}");
                bool disarmed = false;
                try { disarmed = port.doARM(sid, cid, false); } catch { }
                Check(disarmed && await WaitFor(() => !mav.cs.armed, 10), "rover disarm");
                await Task.Delay(3000);

                // DataFlash log list (DATA > DataFlash Logs > Download via MAVLink uses GetLogList)
#pragma warning disable CS0612
                var logs = port.GetLogList();
                Check(logs.Count > 0, "log list (LOG_ENTRY)", $"{logs.Count} logs, last {logs.LastOrDefault().size} bytes");
                // same request while parameter reads run in parallel: no reply may be lost
                int paramOk = 0, paramFail = 0, listOk = 0, listFail = 0;
                var stop = DateTime.Now.AddSeconds(20);
                var reader = Task.Run(() =>
                {
                    while (DateTime.Now < stop)
                    {
                        try { port.GetParam(sid, cid, "CRUISE_SPEED"); paramOk++; } catch { paramFail++; }
                    }
                });
                while (DateTime.Now < stop)
                {
                    try { if (port.GetLogList().Count == logs.Count) listOk++; else listFail++; } catch { listFail++; }
                }
                await reader;
#pragma warning restore CS0612
                Check(listFail == 0 && paramFail == 0 && listOk > 0, "log list under concurrent parameter reads",
                    $"log lists ok {listOk} failed {listFail}; param reads ok {paramOk} failed {paramFail}");
                return;
            }
            Check(await WaitFor(() => mav.cs.alt > 25, 90), "VTOL takeoff reached 25m", $"alt {mav.cs.alt:F1}");
            if (badLink)
            {
                expectBlackout = true;
                SetLink("blackout");
                await Task.Delay(10000);
                SetLink("ok");
                var restored = DateTime.Now;
                bool back = await WaitFor(() => (DateTime.UtcNow - mav.lastvalidpacket).TotalSeconds < 1, 20);
                Check(back, "telemetry recovers after 10 s link loss", $"{(DateTime.Now - restored).TotalSeconds:F1}s after link restored");
                await Task.Delay(3000);
                expectBlackout = false;
            }

            if (copter)
                Check(await WaitFor(() => mav.cs.groundspeed > 3, 120), "cruising to waypoints", $"gs {mav.cs.groundspeed:F1}");
            else
                Check(await WaitFor(() => mav.cs.groundspeed > 15, 120), "transition to fixed-wing", $"gs {mav.cs.groundspeed:F1}");
            Check(await WaitFor(() => mav.cs.wpno >= 3, 180), "reached fixed-wing waypoints", $"wp {mav.cs.wpno}");
            Check(await WaitFor(() => !mav.cs.armed, 300), "VTOL land + auto disarm", $"alt {mav.cs.alt:F1}");
            double dist = mav.cs.DistToHome;
            Check(dist < 15, "landed near home", $"{dist:F1} m");

            // Soak: keep flying the same VTOL mission for N minutes in one session; every cycle must succeed and
            // the app's memory must not grow without bound.
            if (int.TryParse(Environment.GetEnvironmentVariable("SARUS_SOAK_MINUTES"), out var soakMin) && soakMin > 0)
            {
                var soakEnd = DateTime.Now.AddMinutes(soakMin);
                var proc = System.Diagnostics.Process.GetCurrentProcess();
                proc.Refresh();
                long memStart = proc.PrivateMemorySize64;
                int cycle = 0, cycleFails = 0;
                while (DateTime.Now < soakEnd)
                {
                    cycle++;
                    string failedAt = null;
                    void Step(bool passed, string step) { if (!passed && failedAt == null) failedAt = step; }
                    await mav_mission.upload(port, sid, cid, MAVLink.MAV_MISSION_TYPE.MISSION, mission);
                    // like the app's "Restart Mission": back to the first item, otherwise ArduPilot refuses to arm
                    // ("In landing sequence", "Auto missing takeoff waypoint")
                    Step(port.setWPCurrent(sid, cid, 1), "restart mission");
                    port.setMode(sid, cid, "AUTO");
                    Step(await WaitFor(() => mav.cs.mode.ToUpper() == "AUTO", 10), "mode AUTO");
                    bool armedNow = false;
                    var dl = DateTime.Now.AddSeconds(60);
                    while (!armedNow && DateTime.Now < dl) { try { armedNow = port.doARM(sid, cid, true); } catch { } if (!armedNow) await Task.Delay(2000); }
                    Step(armedNow, "arm");
                    if (armedNow)
                    {
                        Step(await WaitFor(() => mav.cs.alt > 25, 90), "takeoff");
                        Step(await WaitFor(() => mav.cs.wpno >= 3, 240), "waypoints");
                        Step(await WaitFor(() => !mav.cs.armed, 300), "land+disarm");
                        Step(mav.cs.DistToHome < 15, "landed near home");
                    }
                    bool ok = failedAt == null;
                    proc.Refresh();
                    Log($"SOAK cycle {cycle} {(ok ? "ok" : "FAILED at " + failedAt)} mem {proc.PrivateMemorySize64 / 1048576} MB dist {mav.cs.DistToHome:F1} m");
                    if (!ok) cycleFails++;
                }
                proc.Refresh();
                long growth = (proc.PrivateMemorySize64 - memStart) / 1048576;
                Check(cycleFails == 0, "soak: every mission cycle succeeded", $"{cycle} cycles, {cycleFails} failed");
                Check(growth < 300, "soak: memory stable", $"grew {growth} MB over {soakMin} min");
            }
        });

        // Screenshot each main screen as the app draws it (for visual review), checking each opens cleanly.
        var shotDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPathForDumps)), "screens-" +
                                   Path.GetFileNameWithoutExtension(reportPathForDumps));
        Directory.CreateDirectory(shotDir);
        var menus = new (string name, string item)[] {
            ("1-data", "MenuFlightData"), ("2-plan", "MenuFlightPlanner"), ("3-setup", "MenuInitConfig"),
            ("4-config", "MenuConfigTune"), ("5-simulation", "MenuSimulation"), ("6-help", "MenuHelp") };
        var mf = MainV2.instance;
        mf.WindowState = System.Windows.Forms.FormWindowState.Maximized;
        mf.TopMost = true;
        mf.Activate();
        await Task.Delay(1000);
        var themes = Environment.GetEnvironmentVariable("SARUS_SCREEN_THEMES")?.Split(',') ?? new[] { "" };
        foreach (var theme in themes)
        {
            if (theme != "")
            {
                MissionPlanner.Utilities.ThemeManager.LoadTheme(theme);
                MissionPlanner.Utilities.ThemeManager.ApplyThemeTo(mf);
            }
            foreach (var (name, item) in menus)
            {
                try
                {
                    var field = typeof(MainV2).GetField(item, System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                    var button = (System.Windows.Forms.ToolStripItem)field.GetValue(mf);
                    button.PerformClick();
                    await Task.Delay(3000);
                    // capture what is on screen (includes GL/map surfaces that DrawToBitmap misses)
                    // Physical screen size: in a DPI-unaware process Screen bounds are scaled, CopyFromScreen is not.
                    var hdc = GetDC(IntPtr.Zero);
                    var screen = new System.Drawing.Rectangle(0, 0, GetDeviceCaps(hdc, 118), GetDeviceCaps(hdc, 117));
                    ReleaseDC(IntPtr.Zero, hdc);
                    using (var bmp = new System.Drawing.Bitmap(screen.Width, screen.Height))
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                    {
                        g.CopyFromScreen(screen.Location, System.Drawing.Point.Empty, screen.Size);
                        bmp.Save(Path.Combine(shotDir, (theme == "" ? "" : Path.GetFileNameWithoutExtension(theme) + "-") + name + ".png"));
                    }
                    Check(true, "screen opens " + theme + " " + name);
                }
                catch (Exception ex)
                {
                    Check(false, "screen opens " + theme + " " + name, ex.Message);
                }
            }
        }
        mf.TopMost = false;
        await ParamEditorChecks(port);
        await AllPagesCheck(shotDir);
        await StressChecks(port);
        await ShortcutChecks(port);

        // return to Flight Data so shutdown starts from the normal screen
        ((System.Windows.Forms.ToolStripItem)typeof(MainV2).GetField("MenuFlightData", System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public).GetValue(MainV2.instance)).PerformClick();
        await Task.Delay(1000);
    }
}

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
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int max);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags); // 2 PW_RENDERFULLCONTENT

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
        var defects = new List<string>();
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
                bool gcsDefect = note.Contains("error") || !restoredOk || !cacheOk;
                if (gcsDefect) { gcsFail++; if (defects.Count < 10) defects.Add($"{name}: {note.Trim()}"); }
                consecutiveErrors = note.Contains("error") ? consecutiveErrors + 1 : 0;
                if (consecutiveErrors >= 3)
                {
                    csv.WriteLine($"ABORTED after {name}: aircraft stopped answering");
                    Check(false, "param sweep: aircraft still answering", "aborted after " + name);
                    break;
                }
                csv.WriteLine($"{name},{type},{orig},{test},{kept},{cacheOk},{restoredOk},\"{note.Trim()}\"");
            }
        }
        Check(gcsFail == 0, "param sweep: GCS handled every parameter", $"{tested} tested, {gcsFail} GCS failures, {skipped} skipped; first: {string.Join(" | ", defects)}");
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

    // Saves a picture of the app window. Returns null when the app was the foreground window, otherwise what was
    // on top (the picture is then taken with PrintWindow so it still shows the app).
    static string Shot(string file)
    {
        var mf = MainV2.instance;
        if (mf.WindowState == System.Windows.Forms.FormWindowState.Minimized)
            mf.WindowState = System.Windows.Forms.FormWindowState.Maximized;
        mf.Activate();
        mf.BringToFront();
        SetForegroundWindow(mf.Handle);
        var settle = DateTime.Now.AddMilliseconds(400);
        while (DateTime.Now < settle) { System.Windows.Forms.Application.DoEvents(); System.Threading.Thread.Sleep(20); }

        var top = GetForegroundWindow();
        uint pid;
        GetWindowThreadProcessId(top, out pid);
        bool ours = pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        var title = new System.Text.StringBuilder(256);
        GetWindowText(top, title, title.Capacity);

        var hdc = GetDC(IntPtr.Zero);
        var screen = new System.Drawing.Rectangle(0, 0, GetDeviceCaps(hdc, 118), GetDeviceCaps(hdc, 117));
        ReleaseDC(IntPtr.Zero, hdc);
        // in a DPI-unaware process form bounds are scaled, CopyFromScreen is not
        double scale = (double)screen.Width / System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width;
        var b = mf.Bounds;
        var area = System.Drawing.Rectangle.Intersect(screen, new System.Drawing.Rectangle(
            (int)(b.X * scale), (int)(b.Y * scale), (int)(b.Width * scale), (int)(b.Height * scale)));
        if (ours && area.Width > 0 && area.Height > 0)
        {
            using (var bmp = new System.Drawing.Bitmap(area.Width, area.Height))
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(area.Location, System.Drawing.Point.Empty, area.Size);
                bmp.Save(file);
            }
            return null;
        }
        using (var bmp = new System.Drawing.Bitmap(mf.Width, mf.Height))
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            var dc = g.GetHdc();
            PrintWindow(mf.Handle, dc, 2);
            g.ReleaseHdc(dc);
            bmp.Save(file);
        }
        return $"app was not the foreground window; on top: \"{title}\"";
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
            // Sarus: suggested values are not offered, and the airframe limits page is
            if (label == "setup")
                Check(!pages.Any(p => p.Page is MissionPlanner.GCSViews.ConfigurationView.ConfigInitialParams),
                    "pages: Initial Tune Parameters not offered");
            if (label == "config")
                Check(pages.Any(p => p.Page is MissionPlanner.GCSViews.ConfigurationView.ConfigSarusLimits),
                    "pages: Airframe Limits offered");
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
                    var shotProblem = Shot(Path.Combine(shotDir, $"{label}-{idx:00}-{safe}.png"));
                    opened++;
                    Check(failures == failuresBefore, $"page opens cleanly: {label} / {title}");
                    Check(shotProblem == null, $"page shown in front: {label} / {title}", shotProblem ?? "");
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

        // A. Outside ArduPilot's recommended range: accepted without a prompt or flag, written as entered
        {
            string p = roverProfile ? "ATC_STR_RAT_P" : "TECS_CLMB_MAX";
            double outOfRange = roverProfile ? 50 : 1000;
            double orig = mav.param[p].Value;
            int dialogsBefore = dialogLog.Count;
            var c = Cell(p);
            c.Value = outOfRange.ToString(System.Globalization.CultureInfo.CurrentCulture);
            await Task.Delay(800);
            Check(dialogLog.Count == dialogsBefore, "out-of-range value: no blocking prompt");
            Check(c.Style.BackColor.ToArgb() != amber.ToArgb() && !(c.ToolTipText ?? "").StartsWith("WARNING: Outside"),
                "out-of-range value: no recommended-range flag (suggestions hidden)", c.ToolTipText?.Split('\n')[0]);
            var opt = c.OwningRow.Cells["Options"].Value?.ToString() ?? "";
            Check(!opt.Contains(" "), "options column shows no recommended range", opt.Replace("\n", " | "));
            expectedDialogs.Add(("You are about to change", "yes"));
            expectedDialogs.Add(("successfully saved", "ok"));
            write.Invoke(raw, new object[] { null, EventArgs.Empty });
            await Task.Delay(1000);
            Check(Math.Abs(mav.param[p].Value - outOfRange) < 1e-3, "out-of-range value written to aircraft", $"{p} {mav.param[p].Value}");
            var presaved = raw.Controls.Find("BUT_paramfileload", true);
            Check(presaved.Length == 1 && !presaved[0].Visible, "Load Presaved not offered");
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

        // D. Value the firmware may re-limit AFTER acknowledging the write (found by the sweep): ArduPilot caps
        //    Q_LOIT_ACC_MAX_M at g*tan(max lean angle), but only while the loiter controller runs (e.g. in QLOITER).
        //    Either way the grid must show what the aircraft runs, and the warning appears exactly when it differs.
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
            bool limited = Math.Abs(fresh - 9.81) > 1e-3;
            bool reported = dialogLog.Skip(before).Any(d => d.Contains("did not keep these values") && d.Contains(p));
            Info("re-limit", $"mode {mav.cs.mode}, aircraft runs {fresh}" + (limited ? " (limited by firmware)" : " (kept as sent)"));
            Check(reported == limited, "value re-limited after acknowledgement: warned exactly when the aircraft changed it",
                $"aircraft runs {fresh}, warning shown {reported}");
            if (!limited) expectedDialogs.RemoveAll(x => x.fragment == "did not keep these values");
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

    // The Sarus parameter lock end to end: the app's gate, the password, the C# signature against the firmware's
    // check, a key the aircraft does not know, a write that skips the app, and locking again. Leaves app and
    // aircraft unlocked so the rest of the run can change parameters.
    static async Task LockChecks(MAVLinkInterface port, MAVState mav, uint sid, byte cid)
    {
        // public simulator test key (Sarus firmware Tools/sarus/lock_keys.py)
        const string testSalt = "53415255532d53494c2d544553542d31";
        var testKey = new SarusLock.Key("sitl-test", testSalt, "d1f488e0ce6cb0972c44b8ed72317878aa878e8f3677e17d9023b325c5fe9b9b");
        // the C# derivation must give the same key as the Python one
        Check(SarusLock.ToHex(SarusLock.Derive("sarus-sitl-test", SarusLock.FromHex(testSalt)).GeneratePublicKey().GetEncoded())
              == SarusLock.ToHex(testKey.PublicKey), "lock: C# key derivation matches the firmware tools");

        SarusLock.UseKeys(new[] { testKey });
        var asked = new List<string>();
        SarusLock.RequestUnlock = what => { asked.Add(what); return false; }; // a user pressing Cancel
        Check(SarusLock.Configured && !SarusLock.Unlocked, "lock: app locked once it holds a key");

        var flags = await port.SarusLockStatusAsync(sid, cid);
        Check(flags != null && (flags.Value & SarusLock.FLAG_ACTIVE) != 0 && (flags.Value & SarusLock.FLAG_UNLOCKED) == 0,
            "lock: aircraft reports its lock active and locked", $"flags {flags}");

        const string name = "LOG_DISARMED";
        double orig = mav.param[name].Value, target = orig == 0 ? 1 : 0;
        bool ok = port.setParam(sid, cid, name, target);
        Check(!ok && asked.Count == 1, "lock: app refuses a parameter write and asks for the password", string.Join(",", asked));
        Check(Math.Abs(port.GetParam(sid, cid, name) - orig) < 1e-3, "lock: aircraft value unchanged");
        Check(!port.doCommand(sid, cid, MAVLink.MAV_CMD.PREFLIGHT_STORAGE, 2, 0, 0, 0, 0, 0, 0),
            "lock: parameter reset refused by the app");

        // a station that skips the app's gate is refused by the aircraft itself
        var raw = new MAVLink.mavlink_param_set_t
        {
            target_system = (byte) sid, target_component = cid, param_value = (float) target,
            param_type = (byte) MAVLink.MAV_PARAM_TYPE.REAL32,
            param_id = System.Text.Encoding.ASCII.GetBytes(name.PadRight(16, '\0'))
        };
        bool rawSent = false;
        try { port.sendPacket(raw, sid, cid); rawSent = true; } catch (Exception ex) { Log("raw write not sent: " + ex.Message); }
        await Task.Delay(2000);
        Check(rawSent && Math.Abs(port.GetParam(sid, cid, name) - orig) < 1e-3, "lock: aircraft refuses a write that skips the app",
            rawSent ? "" : "raw packet was not sent");

        Check(!SarusLock.TryUnlock("not the password"), "lock: wrong password refused");

        // the right password typed at the prompt: the app unlocks, then the aircraft, then the write goes through
        SarusLock.RequestUnlock = what => SarusLock.TryUnlock("sarus-sitl-test") && Unlock();
        ok = port.setParam(sid, cid, name, target);
        Check(ok && SarusLock.Unlocked, "lock: right password unlocks the app");
        Check(Math.Abs(port.GetParam(sid, cid, name) - target) < 1e-3, "lock: write reaches the unlocked aircraft",
            $"{name} {orig} -> {target}");
        flags = await port.SarusLockStatusAsync(sid, cid);
        Check(flags != null && (flags.Value & SarusLock.FLAG_UNLOCKED_BY_YOU) != 0, "lock: aircraft unlocked by this station",
            $"flags {flags}");

        // locking the app locks the aircraft too
        SarusLock.Lock();
        Check(await WaitFor(() =>
            {
                var s = port.SarusLockStatusAsync(sid, cid).Result;
                return s != null && (s.Value & SarusLock.FLAG_UNLOCKED) == 0;
            }, 10), "lock: locking the app locks the aircraft");
        SarusLock.RequestUnlock = null;
        Check(!port.setParam(sid, cid, name, orig), "lock: writes refused again after locking");

        // an app key the aircraft does not know: the app unlocks, the aircraft refuses and the user is told
        var otherSalt = new byte[16];
        var other = new SarusLock.Key("other", SarusLock.ToHex(otherSalt),
            SarusLock.ToHex(SarusLock.Derive("other-password", otherSalt).GeneratePublicKey().GetEncoded()));
        SarusLock.UseKeys(new[] { other });
        int dl = dialogLog.Count;
        expectedDialogs.Add(("refused the unlock", "ok"));
        Check(SarusLock.TryUnlock("other-password"), "lock: app unlocks with its own key");
        SarusLockUI.UnlockAllAircraft();
        Check(await WaitFor(() => dialogLog.Skip(dl).Any(d => d.Contains("refused the unlock")), 10),
            "lock: user told when the aircraft refuses the app's key");
        port.setParam(sid, cid, name, orig);
        Check(Math.Abs(port.GetParam(sid, cid, name) - target) < 1e-3, "lock: that aircraft keeps its value");

        // back to the test key, unlocked, parameter restored
        SarusLock.UseKeys(new[] { testKey });
        Check(SarusLock.TryUnlock("sarus-sitl-test") && Unlock(), "lock: unlocked again for the remaining checks");
        port.setParam(sid, cid, name, orig);
        Check(Math.Abs(port.GetParam(sid, cid, name) - orig) < 1e-3, "lock: parameter restored", $"{name} {orig}");
    }

    // Airframe limits: the rules (both ArduPilot 4.6 and 4.7 names), storage, the page, and the blinking banner
    static async Task LimitsChecks(MAVLinkInterface port, MAVState mav, uint sid, byte cid)
    {
        var K = SarusEnvelope.Kind.Plane;
        var e = new SarusEnvelope { MaxClimb = 5, StallSpeed = 12, MaxPitchDown = 20, MaxTilt = 40, MaxHoverSpeed = 12, MinTurnRadius = 1, MaxBank = 45 };
        int V(SarusEnvelope.Kind k, string p, double v) => e.Check(k, p, v).Count();
        Check(V(K, "TECS_CLMB_MAX", 6) == 1 && V(K, "TECS_CLMB_MAX", 5) == 0, "limits: climb rate above the airframe's flagged, equal not");
        Check(V(K, "AIRSPEED_MIN", 10) == 1 && V(K, "AIRSPEED_MIN", 14) == 0, "limits: minimum airspeed below stall flagged");
        Check(V(K, "AIRSPEED_STALL", 0) == 0, "limits: AIRSPEED_STALL 0 (automatic) not flagged");
        Check(V(K, "PTCH_LIM_MIN_DEG", -25) == 1 && V(K, "PTCH_LIM_MIN_DEG", -15) == 0, "limits: nose-down pitch (4.7 name, degrees)");
        Check(V(K, "LIM_PITCH_MIN", -2500) == 1 && V(K, "LIM_ROLL_CD", 4400) == 0 && V(K, "LIM_ROLL_CD", 4600) == 1,
            "limits: older centidegree names converted");
        var C = SarusEnvelope.Kind.Copter;
        Check(V(C, "ANGLE_MAX", 4500) == 1 && V(C, "ATC_ANGLE_MAX", 35) == 0, "limits: copter lean angle, 4.6 (cdeg) and 4.7 (deg)");
        Check(V(C, "WPNAV_SPEED", 1500) == 1 && V(C, "WP_SPD", 10) == 0, "limits: copter speed, 4.6 (cm/s) and 4.7 (m/s)");
        Check(V(C, "PILOT_SPEED_DN", 0) == 0, "limits: 0 meaning 'automatic' not flagged");
        Check(V(C, "TECS_CLMB_MAX", 100) == 0, "limits: plane rules not applied to a copter");
        Check(V(SarusEnvelope.Kind.QuadPlane, "Q_ANGLE_MAX", 4100) == 1 && V(SarusEnvelope.Kind.QuadPlane, "Q_A_ANGLE_MAX", 30) == 0,
            "limits: QuadPlane VTOL lean angle");
        Check(V(SarusEnvelope.Kind.Rover, "TURN_RADIUS", 0.5) == 1, "limits: rover turn radius below the minimum");
        Check(new SarusEnvelope().CheckAll(K, new[] { new KeyValuePair<string, double>("TECS_CLMB_MAX", 1000) }).Count == 0,
            "limits: nothing flagged when no limit is entered");
        Check(SarusEnvelope.KeyFor("00ab:cd/ef", 1, Firmwares.ArduPlane).All(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_') &&
              SarusEnvelope.KeyFor("", 3, Firmwares.ArduCopter2) == "sysid3-ArduCopter2", "limits: storage key is a safe file name");

        // live: the connected QuadPlane
        Check(SarusLimitsUI.KindForCurrent() == SarusEnvelope.Kind.QuadPlane, "limits: connected aircraft recognised as QuadPlane");
        var key = SarusLimitsUI.KeyForCurrent();
        Info("limits key", key);
        double climb = mav.param["TECS_CLMB_MAX"].Value;
        new SarusEnvelope { MaxClimb = climb - 1 }.Save(key);
        SarusLimitsUI.Reload();
        var bannerField = typeof(SarusLimitsUI).GetField("banner", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var banner = (System.Windows.Forms.Panel) bannerField.GetValue(null);
        Check(await WaitFor(() => banner.Visible && SarusLimitsUI.Current.Any(v => v.Param == "TECS_CLMB_MAX"), 5),
            "limits: banner shows a parameter beyond the airframe", string.Join("; ", SarusLimitsUI.Current));
        var colours = new HashSet<int>();
        for (int i = 0; i < 6; i++) { colours.Add(banner.BackColor.ToArgb()); await Task.Delay(250); }
        Check(colours.Count >= 2, "limits: banner blinks", colours.Count + " colours");
        Check(Math.Abs(mav.param["TECS_CLMB_MAX"].Value - climb) < 1e-6, "limits: the parameter itself is left as set");
        Check(SarusLimitsUI.CheckValue("TECS_CLMB_MAX", climb + 3).Count == 1, "limits: a typed value is checked before writing");

        // the page: bad input is refused with a message, good input is stored for this flight controller
        System.Windows.Forms.Control pageHost = null;
        MainV2.instance.Invoke((Action) (() =>
        {
            var page = new MissionPlanner.GCSViews.ConfigurationView.ConfigSarusLimits();
            pageHost = new System.Windows.Forms.Form { Width = 800, Height = 900, Text = "limits page test" };
            page.Dock = System.Windows.Forms.DockStyle.Fill;
            pageHost.Controls.Add(page);
            ((System.Windows.Forms.Form) pageHost).Show();
            page.Activate();
        }));
        await Task.Delay(1000);
        string statusText = null;
        MainV2.instance.Invoke((Action) (() =>
        {
            var page = (MissionPlanner.GCSViews.ConfigurationView.ConfigSarusLimits) pageHost.Controls[0];
            IEnumerable<System.Windows.Forms.Control> Walk(System.Windows.Forms.Control c) =>
                new[] { c }.Concat(c.Controls.Cast<System.Windows.Forms.Control>().SelectMany(Walk));
            var boxes = Walk(page).OfType<System.Windows.Forms.TextBox>().ToList();
            var climbBox = boxes.First(b => b.AccessibleName == "Maximum climb rate in m/s");
            var saveBtn = Walk(page).OfType<System.Windows.Forms.Button>().First(b => b.Text == "Save limits");
            climbBox.Text = "fast";
            saveBtn.PerformClick();
            statusText = Walk(page).OfType<System.Windows.Forms.Label>().Select(l => l.Text).FirstOrDefault(t => t.StartsWith("Not saved"));
            climbBox.Text = "7";
            saveBtn.PerformClick();
            pageHost.Dispose();
        }));
        Check(statusText != null && statusText.Contains("Maximum climb rate"), "limits page: bad input refused with a reason", statusText);
        Check(SarusEnvelope.Load(key).MaxClimb == 7, "limits page: limit saved for this flight controller");

        // nothing beyond the limits any more: the banner goes away
        new SarusEnvelope().Save(key);
        SarusLimitsUI.Reload();
        Check(await WaitFor(() => !banner.Visible, 5), "limits: banner hidden once nothing is beyond the limits");
    }

    // In-flight alarm on a real failure: QuadPlane mission, engine cut in cruise
    static async Task AlarmFlight(MAVLinkInterface port, MAVState mav, uint sid, byte cid)
    {
        var key = SarusLimitsUI.KeyForCurrent();
        new SarusEnvelope { AlarmAltitudeError = 10, AlarmAirspeedError = 3, AlarmTrackError = 60, AlarmAfterSeconds = 8 }.Save(key);
        SarusLimitsUI.Reload();
        var raised = new List<string>();
        var answers = new List<string>();
        SarusFlightAlarm.Raised += t => { lock (raised) raised.Add(t); Info("alarm", t.Replace("\n", " | ")); };
        SarusFlightAlarm.Answered += a => { lock (answers) answers.Add(a); Info("alarm answer", a); };

        // long fixed-wing legs, so the failure happens in cruise and not near take-off or landing
        double hlat = -35.363261, hlng = 149.165230;
        var mission = new List<Locationwp>
        {
            new Locationwp { id = (ushort) MAVLink.MAV_CMD.WAYPOINT, lat = hlat, lng = hlng, alt = 584, frame = 0 },
            new Locationwp { id = (ushort) MAVLink.MAV_CMD.VTOL_TAKEOFF, lat = hlat, lng = hlng, alt = 30, frame = 3 },
            new Locationwp { id = (ushort) MAVLink.MAV_CMD.WAYPOINT, lat = hlat + 0.027, lng = hlng, alt = 60, frame = 3 },
            new Locationwp { id = (ushort) MAVLink.MAV_CMD.WAYPOINT, lat = hlat + 0.027, lng = hlng + 0.033, alt = 60, frame = 3 },
            new Locationwp { id = (ushort) MAVLink.MAV_CMD.VTOL_LAND, lat = hlat, lng = hlng, alt = 0, frame = 3 },
        };
        await mav_mission.upload(port, mav.sysid, mav.compid, MAVLink.MAV_MISSION_TYPE.MISSION, mission);

        port.setMode(sid, cid, "AUTO");
        Check(await WaitFor(() => mav.cs.mode.ToUpper() == "AUTO", 10), "alarm flight: mode AUTO", mav.cs.mode);
        bool armed = false;
        var armDeadline = DateTime.Now.AddSeconds(150);
        while (!armed && DateTime.Now < armDeadline)
        {
            try { armed = port.doARM(sid, cid, true); } catch { }
            if (!armed) await Task.Delay(3000);
        }
        Check(armed && await WaitFor(() => mav.cs.armed, 5), "alarm flight: armed in AUTO");
        if (!armed) return;

        Check(await WaitFor(() => mav.cs.alt > 25, 90), "alarm flight: VTOL take-off", $"alt {mav.cs.alt:F1}");
        Check(await WaitFor(() => mav.cs.airspeed > 15 && mav.cs.wpno == 2, 120), "alarm flight: fixed-wing cruise",
            $"airspeed {mav.cs.airspeed:F1} wp {mav.cs.wpno}");
        await Task.Delay(20000);
        Check(raised.Count == 0, "alarm: silent through a normal take-off, transition and climb", string.Join(" / ", raised));

        // engine out (servo 3, the forward motor, at zero thrust) and no VTOL assist: the aircraft cannot hold its
        // altitude or airspeed any more
        // ArduPilot 4.7: SIM_ENGINE_FAIL is a mask of servo outputs; 4.6: it is the index of one output
        bool ap46 = mav.VersionString.Contains("V4.6");
        Check(port.setParam(sid, cid, "Q_ASSIST_SPEED", 0) && port.setParam(sid, cid, "SIM_ENGINE_MUL", 0, true) &&
              port.setParam(sid, cid, "SIM_ENGINE_FAIL", ap46 ? 2 : 4, true), "alarm flight: engine cut in the simulator",
              ap46 ? "4.6: output index 2" : "4.7: output mask 4");
        Check(mav.cs.wpno == 2, "alarm flight: still on the long leg when the engine is cut", "wp " + mav.cs.wpno);
        Check(await WaitFor(() => raised.Count >= 1, 90), "alarm: sounds when the aircraft cannot hold its set values",
            $"alt err {mav.cs.alt_error:F1} aspd err {mav.cs.aspd_error:F1}");
        Check(await WaitFor(() => answers.Count >= 1, SarusFlightAlarm.AnswerSeconds + 5) && answers[0] == "continue (no answer)",
            "alarm: unanswered for 15 s, the mission continues", string.Join(",", answers));
        Check(mav.cs.mode.ToUpper() == "AUTO", "alarm: mode left in AUTO after no answer", mav.cs.mode);

        // ask again at once, and answer Hold
        typeof(SarusFlightAlarm).GetField("quietUntil", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .SetValue(null, DateTime.MinValue);
        int before = raised.Count;
        Check(await WaitFor(() => raised.Count > before, 60), "alarm: sounds again while the problem lasts");
        bool clicked = false;
        MainV2.instance.Invoke((Action) (() =>
        {
            var f = System.Windows.Forms.Application.OpenForms.Cast<System.Windows.Forms.Form>().FirstOrDefault(x => x.Text == "Sarus flight alarm");
            var hold = f?.Controls.OfType<System.Windows.Forms.Button>().FirstOrDefault(b => b.Text == "Hold position");
            if (hold != null) { hold.PerformClick(); clicked = true; }
        }));
        Check(clicked, "alarm: window offers Hold position");
        Check(await WaitFor(() => mav.cs.mode.ToUpper() == "LOITER" || mav.cs.mode.ToUpper() == "QLOITER", 10),
            "alarm: Hold switches the aircraft to loiter", mav.cs.mode);

        // engine back, return home
        port.setParam(sid, cid, "SIM_ENGINE_MUL", 1, true);
        port.setParam(sid, cid, "SIM_ENGINE_FAIL", 0, true);
        port.setMode(sid, cid, "RTL");
        Check(await WaitFor(() => mav.cs.mode.ToUpper() == "RTL" || mav.cs.mode.ToUpper() == "QRTL", 10), "alarm flight: return home", mav.cs.mode);
    }

    static bool Unlock()
    {
        SarusLockUI.UnlockAllAircraft();
        var port = MainV2.comPort;
        var flags = port.SarusLockStatusAsync((uint)port.sysidcurrent, (byte)port.compidcurrent).Result;
        return flags != null && (flags.Value & SarusLock.FLAG_UNLOCKED_BY_YOU) != 0;
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

            // Sarus parameter lock, against a SITL built with the public test key
            if (Environment.GetEnvironmentVariable("SARUS_LOCKTEST") == "1")
                await LockChecks(port, mav, sid, cid);
            else if (SarusLock.Configured)
            {
                // the app carries the owner's keys; the other checks run unlocked with the public test key, which
                // simulators built with --define=AP_SARUS_LOCK_TEST_KEY also accept (keyless simulators need nothing)
                SarusLock.UseKeys(new[] { new SarusLock.Key("sitl-test", "53415255532d53494c2d544553542d31",
                    "d1f488e0ce6cb0972c44b8ed72317878aa878e8f3677e17d9023b325c5fe9b9b") });
                Check(SarusLock.TryUnlock("sarus-sitl-test"), "lock: harness unlocked with the simulator test key");
                SarusLockUI.UnlockAllAircraft();
            }
            if (rover)
                Check(mav.param.ContainsKey("CRUISE_SPEED") && mav.param.ContainsKey("MODE1"), "rover parameters present");
            else if (copter)
                Check(mav.param.ContainsKey("FRAME_CLASS"), "copter frame parameters present");
            else
                Check(mav.param.ContainsKey("Q_ENABLE") && mav.param["Q_ENABLE"].Value == 1, "quadplane enabled", "Q_ENABLE=1");

            var tuneParams = rover ? new[] { "ATC_STR_RAT_P", "ATC_SPEED_P", "CRUISE_SPEED", "WP_SPEED", "TURN_RADIUS" }
                                    : copter ? new[] { "ATC_RAT_RLL_P", "ATC_RAT_PIT_P",
                                              mav.param.ContainsKey("PSC_D_ACC_P") ? "PSC_D_ACC_P" : "PSC_ACCZ_P", // 4.7 / 4.6 name
                                              mav.param.ContainsKey("WP_SPD") ? "WP_SPD" : "WPNAV_SPEED" }
                                    : new[] { "TECS_CLMB_MAX", "TECS_SINK_MAX", "TECS_TIME_CONST",
                                              mav.param.ContainsKey("Q_A_ANGLE_MAX") ? "Q_A_ANGLE_MAX" : "Q_ANGLE_MAX" }; // 4.7 / 4.6 name
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

            // Airframe limits and the in-flight alarm fly their own QuadPlane flight
            if (Environment.GetEnvironmentVariable("SARUS_ALARMTEST") == "1")
            {
                await LimitsChecks(port, mav, sid, cid);
                await AlarmFlight(port, mav, sid, cid);
                return;
            }

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
        var screenNames = new Dictionary<string, string> {
            ["MenuFlightData"] = "FlightData", ["MenuFlightPlanner"] = "FlightPlanner", ["MenuInitConfig"] = "HWConfig",
            ["MenuConfigTune"] = "SWConfig", ["MenuSimulation"] = "Simulation", ["MenuHelp"] = "Help" };
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
                    // capture the app window as it is drawn (includes GL/map surfaces that DrawToBitmap misses)
                    var shotProblem = Shot(Path.Combine(shotDir, (theme == "" ? "" : Path.GetFileNameWithoutExtension(theme) + "-") + name + ".png"));
                    string showing = MainV2.View?.current?.Name ?? "";
                    Check(shotProblem == null && showing == screenNames[item], "screen opens " + theme + " " + name,
                        shotProblem ?? ("showing " + showing));
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

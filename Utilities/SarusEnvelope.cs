using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using log4net;
using MissionPlanner.ArduPilot;
using Newtonsoft.Json;

namespace MissionPlanner.Utilities
{
    /// <summary>
    /// Sarus airframe limits: what this airframe can physically do, entered by the user, and the parameter values
    /// that ask for more than that. The user stays free to set any value; a value beyond these limits is shown as a
    /// blinking red alert (SarusLimitsUI), never changed or refused.
    /// </summary>
    public class SarusEnvelope
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        // all in SI units: m/s, degrees, metres. Null means "not entered", and its rules are skipped.
        public double? StallSpeed;      // fixed wing: slowest speed it stays flying at
        public double? MaxSpeed;        // fixed wing airspeed, or rover ground speed
        public double? MaxClimb;        // fixed wing climb rate
        public double? MaxSink;         // fixed wing descent rate
        public double? MaxBank;         // fixed wing bank angle
        public double? MaxPitchUp;      // fixed wing nose-up attitude
        public double? MaxPitchDown;    // fixed wing nose-down attitude, as a positive number
        public double? MaxTilt;         // multirotor / VTOL lean angle
        public double? MaxHoverSpeed;   // multirotor / VTOL horizontal speed
        public double? MaxHoverClimb;   // multirotor / VTOL climb rate
        public double? MaxHoverSink;    // multirotor / VTOL descent rate
        public double? MinTurnRadius;   // rover

        // in-flight alarm (SarusFlightAlarm): how far off, for how long, before the pilot is asked
        public bool AlarmEnabled = true;
        public double AlarmAltitudeError = 15;  // m from the altitude the aircraft is trying to hold
        public double AlarmAirspeedError = 4;   // m/s from the airspeed it is trying to hold
        public double AlarmTrackError = 40;     // m off the planned track
        public int AlarmAfterSeconds = 10;

        public string Name = "";

        public enum Kind
        {
            Plane,
            QuadPlane,
            Copter,
            Rover,
            Other
        }

        /// <summary>one parameter value beyond the airframe's limits</summary>
        public class Violation
        {
            public string Param;
            public double Value;      // as the parameter holds it
            public string Message;    // plain sentence for the alert

            public override string ToString() => Message;
        }

        // a parameter name and the factor that turns its value into SI units
        private class Name_
        {
            public string Param;
            public double ToSI;

            public Name_(string p, double s)
            {
                Param = p;
                ToSI = s;
            }
        }

        private class Rule
        {
            public Kind[] Kinds;
            public Name_[] Names;               // every name this setting has had; all that exist are checked
            public Func<SarusEnvelope, double?> Limit;
            public bool LimitIsMinimum;         // the value must not be below the limit (stall speed, turn radius)
            public bool Negate;                 // value is stored negative (pitch down)
            public string What;                 // "climb rate"
            public string Unit;                 // "m/s"
            public bool ZeroMeansAuto;          // 0 means "use another setting", never a violation
        }

        private const double CM = 0.01, CDEG = 0.01;
        private static readonly Kind[] FW = { Kind.Plane, Kind.QuadPlane };
        private static readonly Kind[] VTOL = { Kind.QuadPlane };
        private static readonly Kind[] MR = { Kind.Copter };
        private static readonly Kind[] RV = { Kind.Rover };

        private static Name_ N(string p, double s = 1) => new Name_(p, s);

        // ArduPilot 4.7 renamed many parameters into SI units; 4.6 names and units are listed next to them
        private static readonly Rule[] Rules =
        {
            // fixed wing
            new Rule { Kinds = FW, Names = new[] { N("AIRSPEED_MIN"), N("ARSPD_FBW_MIN") }, Limit = e => e.StallSpeed,
                LimitIsMinimum = true, What = "stall speed", Unit = "m/s" },
            new Rule { Kinds = FW, Names = new[] { N("AIRSPEED_STALL") }, Limit = e => e.StallSpeed,
                LimitIsMinimum = true, What = "stall speed", Unit = "m/s", ZeroMeansAuto = true },
            new Rule { Kinds = FW, Names = new[] { N("AIRSPEED_CRUISE"), N("TRIM_ARSPD_CM", CM) }, Limit = e => e.StallSpeed,
                LimitIsMinimum = true, What = "stall speed", Unit = "m/s" },
            new Rule { Kinds = FW, Names = new[] { N("AIRSPEED_CRUISE"), N("TRIM_ARSPD_CM", CM) }, Limit = e => e.MaxSpeed,
                What = "maximum airspeed", Unit = "m/s" },
            new Rule { Kinds = FW, Names = new[] { N("AIRSPEED_MAX"), N("ARSPD_FBW_MAX") }, Limit = e => e.MaxSpeed,
                What = "maximum airspeed", Unit = "m/s" },
            new Rule { Kinds = FW, Names = new[] { N("TECS_CLMB_MAX") }, Limit = e => e.MaxClimb,
                What = "maximum climb rate", Unit = "m/s" },
            new Rule { Kinds = FW, Names = new[] { N("TECS_SINK_MAX") }, Limit = e => e.MaxSink,
                What = "maximum descent rate", Unit = "m/s" },
            new Rule { Kinds = FW, Names = new[] { N("ROLL_LIMIT_DEG"), N("LIM_ROLL_CD", CDEG) }, Limit = e => e.MaxBank,
                What = "maximum bank angle", Unit = "deg" },
            new Rule { Kinds = FW, Names = new[] { N("PTCH_LIM_MAX_DEG"), N("LIM_PITCH_MAX", CDEG) }, Limit = e => e.MaxPitchUp,
                What = "maximum nose-up pitch", Unit = "deg" },
            new Rule { Kinds = FW, Names = new[] { N("PTCH_LIM_MIN_DEG"), N("LIM_PITCH_MIN", CDEG) }, Limit = e => e.MaxPitchDown,
                Negate = true, What = "maximum nose-down pitch", Unit = "deg" },

            // VTOL part of a QuadPlane
            new Rule { Kinds = VTOL, Names = new[] { N("Q_A_ANGLE_MAX"), N("Q_ANGLE_MAX", CDEG) }, Limit = e => e.MaxTilt,
                What = "maximum VTOL lean angle", Unit = "deg", ZeroMeansAuto = true },
            new Rule { Kinds = VTOL, Names = new[] { N("Q_WP_SPD"), N("Q_WP_SPEED", CM), N("Q_LOIT_SPEED_MS"), N("Q_LOIT_SPEED", CM) },
                Limit = e => e.MaxHoverSpeed, What = "maximum VTOL speed", Unit = "m/s" },
            new Rule { Kinds = VTOL, Names = new[] { N("Q_PILOT_SPD_UP"), N("Q_WP_SPD_UP"), N("Q_WP_SPEED_UP", CM), N("Q_VELZ_MAX", CM) },
                Limit = e => e.MaxHoverClimb, What = "maximum VTOL climb rate", Unit = "m/s" },
            new Rule { Kinds = VTOL, Names = new[] { N("Q_PILOT_SPD_DN"), N("Q_WP_SPD_DN"), N("Q_WP_SPEED_DN", CM), N("Q_VELZ_MAX_DN", CM) },
                Limit = e => e.MaxHoverSink, What = "maximum VTOL descent rate", Unit = "m/s", ZeroMeansAuto = true },

            // multirotor
            new Rule { Kinds = MR, Names = new[] { N("ATC_ANGLE_MAX"), N("ANGLE_MAX", CDEG) }, Limit = e => e.MaxTilt,
                What = "maximum lean angle", Unit = "deg" },
            new Rule { Kinds = MR, Names = new[] { N("WP_SPD"), N("WPNAV_SPEED", CM), N("LOIT_SPEED_MS"), N("LOIT_SPEED", CM) },
                Limit = e => e.MaxHoverSpeed, What = "maximum speed", Unit = "m/s" },
            new Rule { Kinds = MR, Names = new[] { N("PILOT_SPD_UP"), N("PILOT_SPEED_UP", CM), N("WP_SPD_UP"), N("WPNAV_SPEED_UP", CM) },
                Limit = e => e.MaxHoverClimb, What = "maximum climb rate", Unit = "m/s" },
            new Rule { Kinds = MR, Names = new[] { N("PILOT_SPD_DN"), N("PILOT_SPEED_DN", CM), N("WP_SPD_DN"), N("WPNAV_SPEED_DN", CM) },
                Limit = e => e.MaxHoverSink, What = "maximum descent rate", Unit = "m/s", ZeroMeansAuto = true },

            // rover
            new Rule { Kinds = RV, Names = new[] { N("CRUISE_SPEED"), N("WP_SPEED"), N("SPEED_MAX") }, Limit = e => e.MaxSpeed,
                What = "maximum speed", Unit = "m/s", ZeroMeansAuto = true },
            new Rule { Kinds = RV, Names = new[] { N("TURN_RADIUS") }, Limit = e => e.MinTurnRadius,
                LimitIsMinimum = true, What = "minimum turn radius", Unit = "m", ZeroMeansAuto = true },
        };

        public static Kind KindOf(Firmwares firmware, Func<string, double?> param)
        {
            switch (firmware)
            {
                case Firmwares.ArduPlane:
                    return (param("Q_ENABLE") ?? 0) > 0 ? Kind.QuadPlane : Kind.Plane;
                case Firmwares.ArduCopter2:
                    return Kind.Copter;
                case Firmwares.ArduRover:
                    return Kind.Rover;
                default:
                    return Kind.Other;
            }
        }

        /// <summary>the parameter names these limits cover, for this kind of vehicle</summary>
        public static IEnumerable<string> CoveredParams(Kind kind) =>
            Rules.Where(r => r.Kinds.Contains(kind)).SelectMany(r => r.Names.Select(n => n.Param)).Distinct();

        /// <summary>
        /// Check one parameter value. Returns the violations it causes (normally none or one).
        /// </summary>
        public IEnumerable<Violation> Check(Kind kind, string param, double value)
        {
            foreach (var rule in Rules)
            {
                if (!rule.Kinds.Contains(kind))
                    continue;
                var name = rule.Names.FirstOrDefault(n => n.Param == param);
                if (name == null)
                    continue;
                var limit = rule.Limit(this);
                if (limit == null || limit <= 0)
                    continue;
                if (rule.ZeroMeansAuto && value == 0)
                    continue;

                double si = value * name.ToSI;
                if (rule.Negate)
                    si = -si;
                bool bad = rule.LimitIsMinimum ? si < limit.Value - 1e-6 : si > limit.Value + 1e-6;
                if (!bad)
                    continue;

                string shown = (rule.Negate ? -si : si).ToString("0.##");
                yield return new Violation
                {
                    Param = param,
                    Value = value,
                    Message = rule.LimitIsMinimum
                        ? $"{param} {shown} {rule.Unit} is below this airframe's {rule.What} of {limit.Value:0.##} {rule.Unit}"
                        : $"{param} {shown} {rule.Unit} is above this airframe's {rule.What} of {limit.Value:0.##} {rule.Unit}"
                };
            }
        }

        /// <summary>every violation in a full parameter set</summary>
        public List<Violation> CheckAll(Kind kind, IEnumerable<KeyValuePair<string, double>> parameters)
        {
            var covered = new HashSet<string>(CoveredParams(kind));
            return parameters.Where(p => covered.Contains(p.Key)).SelectMany(p => Check(kind, p.Key, p.Value)).ToList();
        }

        public bool AnyLimitEntered =>
            new[] { StallSpeed, MaxSpeed, MaxClimb, MaxSink, MaxBank, MaxPitchUp, MaxPitchDown, MaxTilt, MaxHoverSpeed,
                MaxHoverClimb, MaxHoverSink, MinTurnRadius }.Any(v => v != null && v > 0);

        // storage: one file per flight controller, under the user's data folder

        public static string Folder => Path.Combine(Settings.GetUserDataDirectory(), "airframes");

        /// <summary>
        /// A key for the physical aircraft: the flight controller's unique id when it reports one, else its system
        /// id and vehicle type.
        /// </summary>
        public static string KeyFor(string uid2, uint sysid, Firmwares firmware)
        {
            var key = !string.IsNullOrWhiteSpace(uid2) && uid2.Trim('0', ' ').Length > 0
                ? "fc-" + uid2.Trim()
                : "sysid" + sysid + "-" + firmware;
            return string.Concat(key.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_'));
        }

        public static SarusEnvelope Load(string key)
        {
            try
            {
                var file = Path.Combine(Folder, key + ".json");
                if (File.Exists(file))
                    return JsonConvert.DeserializeObject<SarusEnvelope>(File.ReadAllText(file)) ?? new SarusEnvelope();
            }
            catch (Exception ex)
            {
                log.Error("airframe limits for " + key + " could not be read", ex);
            }
            return new SarusEnvelope();
        }

        public void Save(string key)
        {
            Directory.CreateDirectory(Folder);
            var file = Path.Combine(Folder, key + ".json");
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(this, Formatting.Indented));
            if (File.Exists(file))
                File.Replace(tmp, file, null);
            else
                File.Move(tmp, file);
        }
    }
}

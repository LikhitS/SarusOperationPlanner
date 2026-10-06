using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using log4net;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace MissionPlanner.ArduPilot
{
    /// <summary>
    /// Sarus parameter lock, ground station side. Viewing and flying stay open; changing the aircraft's setup
    /// (parameters, calibrations, parameter files, firmware, file writes) needs the owner's admin password.
    ///
    /// Nothing secret is stored. Each owner key is an Ed25519 public key plus a salt; typing the right password
    /// re-creates the private key (scrypt N=65536 r=8 p=1, then Ed25519 from that seed), which stays in memory
    /// only while unlocked. Aircraft running Sarus firmware hold the same public keys and accept an unlock only
    /// when this station signs their one-time nonce. The protocol is described in the Sarus firmware,
    /// Tools/sarus/SARUS_LOCK.md, and the key derivation must match Tools/sarus/lock_keys.py there.
    /// </summary>
    public static class SarusLock
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public class Key
        {
            public string Id;
            public byte[] Salt;
            public byte[] PublicKey;

            public Key(string id, string saltHex, string publicHex)
            {
                Id = id;
                Salt = FromHex(saltHex);
                PublicKey = FromHex(publicHex);
            }
        }

        // SECURE_COMMAND operations, "SRL" + n
        public const uint OP_STATUS = 0x53524C01;
        public const uint OP_GET_NONCE = 0x53524C02;
        public const uint OP_UNLOCK = 0x53524C03;
        public const uint OP_LOCK = 0x53524C04;

        // bits in byte 1 of a STATUS or GET_NONCE reply
        public const byte FLAG_ACTIVE = 1;
        public const byte FLAG_UNLOCKED = 2;
        public const byte FLAG_UNLOCKED_BY_YOU = 4;

        public const int NONCE_LEN = 16;

        private static readonly object sync = new object();
        private static List<Key> keys = new List<Key>(SarusLockKeys.Owner);
        private static Ed25519PrivateKeyParameters privateKey;
        private static string unlockedKeyId;

        /// <summary>raised whenever the app is locked or unlocked</summary>
        public static event EventHandler Changed;

        /// <summary>
        /// Asked when a change is attempted while locked, with a short description of the change. The UI shows the
        /// password prompt and returns true if the app is now unlocked. Left null, changes are simply refused.
        /// </summary>
        public static Func<string, bool> RequestUnlock;

        /// <summary>true when this build carries at least one owner key, so the lock is in force</summary>
        public static bool Configured
        {
            get { lock (sync) return keys.Count > 0; }
        }

        /// <summary>true when changes are allowed: no owner key in this build, or the password was given</summary>
        public static bool Unlocked
        {
            get { lock (sync) return keys.Count == 0 || privateKey != null; }
        }

        public static string UnlockedKeyId
        {
            get { lock (sync) return unlockedKeyId; }
        }

        /// <summary>replace the key list; for tests against a simulator built with the public test key</summary>
        public static void UseKeys(IEnumerable<Key> newKeys)
        {
            lock (sync)
            {
                keys = newKeys.ToList();
                privateKey = null;
                unlockedKeyId = null;
            }
            Changed?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>
        /// Check a password against the owner keys. On a match the app is unlocked until Lock() or until it closes.
        /// Takes about half a second per key, by design.
        /// </summary>
        public static bool TryUnlock(string password)
        {
            if (string.IsNullOrEmpty(password))
                return false;

            List<Key> current;
            lock (sync)
                current = keys.ToList();

            foreach (var key in current)
            {
                var priv = Derive(password, key.Salt);
                if (priv.GeneratePublicKey().GetEncoded().SequenceEqual(key.PublicKey))
                {
                    lock (sync)
                    {
                        privateKey = priv;
                        unlockedKeyId = key.Id;
                    }
                    log.Info("Sarus lock: unlocked with key " + key.Id);
                    Changed?.Invoke(null, EventArgs.Empty);
                    return true;
                }
            }

            log.Warn("Sarus lock: wrong password");
            return false;
        }

        public static void Lock()
        {
            bool was;
            lock (sync)
            {
                was = privateKey != null;
                privateKey = null;
                unlockedKeyId = null;
            }
            if (was)
            {
                log.Info("Sarus lock: locked");
                Changed?.Invoke(null, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Gate for every change the app makes to an aircraft. Returns true if the change may go ahead, asking for
        /// the password through RequestUnlock when needed.
        /// </summary>
        public static bool AllowChange(string what)
        {
            if (Unlocked)
                return true;

            log.Info("Sarus lock: " + what + " needs the admin password");
            var ask = RequestUnlock;
            try
            {
                return ask != null && ask(what) && Unlocked;
            }
            catch (Exception ex)
            {
                log.Error(ex);
                return false;
            }
        }

        /// <summary>
        /// Commands that change the aircraft's setup; the same list AP_SarusLock::command_allowed refuses while
        /// locked. A plain reboot or shutdown stays open.
        /// </summary>
        public static bool IsSetupCommand(MAVLink.MAV_CMD cmd, float param1)
        {
            switch (cmd)
            {
                case MAVLink.MAV_CMD.PREFLIGHT_CALIBRATION:
                case MAVLink.MAV_CMD.PREFLIGHT_SET_SENSOR_OFFSETS:
                case MAVLink.MAV_CMD.PREFLIGHT_UAVCAN:
                case MAVLink.MAV_CMD.PREFLIGHT_STORAGE:
                case MAVLink.MAV_CMD.DO_START_MAG_CAL:
                case MAVLink.MAV_CMD.DO_ACCEPT_MAG_CAL:
                case MAVLink.MAV_CMD.FIXED_MAG_CAL:
                case MAVLink.MAV_CMD.FIXED_MAG_CAL_FIELD:
                case MAVLink.MAV_CMD.FIXED_MAG_CAL_YAW:
                case MAVLink.MAV_CMD.ACCELCAL_VEHICLE_POS:
                case MAVLink.MAV_CMD.FLASH_BOOTLOADER:
                case MAVLink.MAV_CMD.STORAGE_FORMAT:
                case MAVLink.MAV_CMD.START_RX_PAIR:
                case MAVLink.MAV_CMD.SCRIPTING:
                    return true;
                case MAVLink.MAV_CMD.PREFLIGHT_REBOOT_SHUTDOWN:
                    return !(param1 == 0 || param1 == 1 || param1 == 2);
                default:
                    return false;
            }
        }

        /// <summary>
        /// The 35-byte block an aircraft expects signed for an unlock; the layout matches SignedBlock in
        /// AP_SarusLock.h.
        /// </summary>
        public static byte[] UnlockBlock(byte targetSystem, byte targetComponent, byte gcsSysid, byte[] nonce)
        {
            if (nonce == null || nonce.Length != NONCE_LEN)
                throw new ArgumentException("nonce must be " + NONCE_LEN + " bytes");
            var block = new byte[35];
            Encoding.ASCII.GetBytes("SARUS-LOCK1").CopyTo(block, 0); // byte 11 stays 0
            BitConverter.GetBytes(OP_UNLOCK).CopyTo(block, 12); // little endian on every platform we run on
            block[16] = targetSystem;
            block[17] = targetComponent;
            block[18] = gcsSysid;
            nonce.CopyTo(block, 19);
            return block;
        }

        /// <summary>signature for an aircraft unlock, or null while the app is locked</summary>
        public static byte[] SignUnlock(byte targetSystem, byte targetComponent, byte gcsSysid, byte[] nonce)
        {
            Ed25519PrivateKeyParameters priv;
            lock (sync)
                priv = privateKey;
            if (priv == null)
                return null;

            var block = UnlockBlock(targetSystem, targetComponent, gcsSysid, nonce);
            var signer = new Ed25519Signer();
            signer.Init(true, priv);
            signer.BlockUpdate(block, 0, block.Length);
            return signer.GenerateSignature();
        }

        public static Ed25519PrivateKeyParameters Derive(string password, byte[] salt)
        {
            var pw = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormC));
            var seed = SCrypt.Generate(pw, salt, 65536, 8, 1, 32);
            Array.Clear(pw, 0, pw.Length);
            var priv = new Ed25519PrivateKeyParameters(seed, 0);
            Array.Clear(seed, 0, seed.Length);
            return priv;
        }

        public static byte[] FromHex(string hex)
        {
            var b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++)
                b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return b;
        }

        public static string ToHex(byte[] b)
        {
            return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
        }
    }
}

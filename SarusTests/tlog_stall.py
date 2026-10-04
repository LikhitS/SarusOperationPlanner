"""For each ~burst in a tlog, compare GCS receive time with the vehicle's own boot clock.
If vehicle time advances ~30s inside a burst received in milliseconds, the GCS stopped reading (backlog).
If vehicle time also jumps by ~0, the vehicle/SITL itself stalled."""
import sys
from datetime import datetime

from pymavlink import mavutil

m = mavutil.mavlink_connection(sys.argv[1], dialect="ardupilotmega")
prev = None
while True:
    msg = m.recv_match(type=["ATTITUDE", "SYSTEM_TIME"], blocking=False)
    if msg is None:
        break
    if msg.get_srcSystem() != 1:
        continue
    boot = msg.time_boot_ms / 1000.0
    rx = msg._timestamp
    if prev:
        drx, dboot = rx - prev[0], boot - prev[1]
        if drx > 2.0 or dboot > 2.0:
            print(f"{datetime.fromtimestamp(prev[0]).strftime('%H:%M:%S.%f')[:-3]} -> "
                  f"{datetime.fromtimestamp(rx).strftime('%H:%M:%S.%f')[:-3]}  "
                  f"gcs-rx-gap={drx:6.1f}s  vehicle-clock-gap={dboot:6.1f}s")
    prev = (rx, boot)

# Within the burst: how much vehicle time was delivered in how little receive time?
print("done")

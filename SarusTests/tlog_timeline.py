"""Print a timeline of vehicle mode/arm state, status texts and command acks from a .tlog."""
import sys
from datetime import datetime

from pymavlink import mavutil

PLANE_MODES = {0: "MANUAL", 5: "FBWA", 10: "AUTO", 11: "RTL", 17: "QSTABILIZE", 18: "QHOVER", 19: "QLOITER",
               20: "QLAND", 21: "QRTL", 2: "STABILIZE", 6: "FBWB"}

path = sys.argv[1]
start = sys.argv[2] if len(sys.argv) > 2 else None
end = sys.argv[3] if len(sys.argv) > 3 else None
m = mavutil.mavlink_connection(path, dialect="ardupilotmega")

last_state = None
last_hb = None
gaps = []
while True:
    msg = m.recv_match(blocking=False)
    if msg is None:
        break
    t = datetime.fromtimestamp(msg._timestamp)
    ts = t.strftime("%H:%M:%S.%f")[:-3]
    hhmmss = t.strftime("%H:%M:%S")
    in_window = (start is None or hhmmss >= start) and (end is None or hhmmss <= end)
    mtype = msg.get_type()
    src = msg.get_srcSystem()

    if mtype == "HEARTBEAT" and src == 1 and msg.get_srcComponent() == 1:
        if last_hb is not None and msg._timestamp - last_hb > 2.0:
            gaps.append((datetime.fromtimestamp(last_hb).strftime("%H:%M:%S"), round(msg._timestamp - last_hb, 1)))
        last_hb = msg._timestamp
        armed = bool(msg.base_mode & 128)
        state = (PLANE_MODES.get(msg.custom_mode, msg.custom_mode), armed)
        if state != last_state and in_window:
            print(f"{ts} VEHICLE mode={state[0]} armed={state[1]}")
        last_state = state
    elif in_window and mtype == "STATUSTEXT":
        print(f"{ts} TEXT    {msg.text}")
    elif in_window and mtype == "COMMAND_ACK":
        print(f"{ts} ACK     cmd={msg.command} result={msg.result}")
    elif in_window and mtype in ("SET_MODE", "COMMAND_LONG", "COMMAND_INT") and src != 1:
        detail = f"cmd={msg.command} p1={msg.param1} p2={msg.param2}" if mtype != "SET_MODE" else f"custom_mode={msg.custom_mode}"
        print(f"{ts} GCS->   {mtype} {detail}")

print("heartbeat gaps > 2s:", gaps if gaps else "none")

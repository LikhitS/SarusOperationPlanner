"""Point app updates at Sarus GitHub Releases and move log files to the Sarus data folder (app.config, MainV2.cs)."""
from pathlib import Path

SRC = Path(r"C:\dev\Sarus\SarusOperationPlanner")
REL = "https://github.com/LikhitS/SarusOperationPlanner/releases"

EDITS = [
    ("app.config", '"https://firmware.oborne.me/MissionPlanner/upgrade/version.txt"', f'"{REL}/latest/download/version.txt"', 1),
    ("app.config", '"https://firmware.ardupilot.org/MissionPlanner/upgrade/"', f'"{REL}/latest/download/SarusOperationPlanner.zip"', 1),
    ("app.config", '"https://firmware.ardupilot.org/MissionPlanner/checksums.txt"', f'"{REL}/latest/download/checksums.txt"', 1),
    ("app.config", '"https://firmware.ardupilot.org/MissionPlanner/MissionPlanner-latest.zip"', f'"{REL}/latest/download/SarusOperationPlanner.zip"', 1),
    ("app.config", '"https://github.com/ArduPilot/MissionPlanner/releases/download/betarelease/version.txt"', f'"{REL}/download/betarelease/version.txt"', 1),
    ("app.config", '"https://github.com/ArduPilot/MissionPlanner/releases/download/betarelease/checksums.txt"', f'"{REL}/download/betarelease/checksums.txt"', 1),
    ("app.config", '"https://github.com/ArduPilot/MissionPlanner/releases/download/betarelease/MissionPlannerBeta.zip"', f'"{REL}/download/betarelease/SarusOperationPlannerBeta.zip"', 1),
    ("app.config", '"https://ci.appveyor.com/api/projects/meee1/missionplanner/artifacts/checksums.txt?branch=master"', f'"{REL}/download/betarelease/checksums.txt"', 1),
    ("app.config", '"https://ci.appveyor.com/api/projects/meee1/missionplanner/artifacts/MissionPlannerBeta.zip?branch=master"', f'"{REL}/download/betarelease/SarusOperationPlannerBeta.zip"', 1),
    ("app.config", r'"${ALLUSERSPROFILE}\\Mission Planner\\MissionPlanner.log"', r'"${ALLUSERSPROFILE}\\Sarus Operation Planner\\SarusOperationPlanner.log"', 1),
    ("app.config", '"/ProgramData/Mission Planner/network.log"', '"/ProgramData/Sarus Operation Planner/network.log"', 1),
    # Windows XP cannot run .NET 4.7.2; never fall back to upstream's XP channel.
    ("MainV2.cs", '"https://firmware.ardupilot.org/MissionPlanner/xp/";', '"";', 2),
    ("MainV2.cs", '"https://firmware.ardupilot.org/MissionPlanner/xp/checksums.txt";', '"";', 1),
]

pending = {}
for rel, old, new, expect in EDITS:
    path = SRC / rel
    data = pending.get(path) or path.read_bytes()
    found = data.count(old.encode())
    if found != expect:
        raise SystemExit(f"ABORT {rel}: expected {expect} x {old!r}, found {found}. Nothing written.")
    pending[path] = data.replace(old.encode(), new.encode())
for path, data in pending.items():
    path.write_bytes(data)
    print("updated", path.name)

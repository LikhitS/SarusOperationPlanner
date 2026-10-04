"""Replace the user-visible product name in a fixed, reviewed list of files.

Code identifiers (MissionPlanner.*), user-agents, file-format identifiers and namespaces are NOT touched.
Each replacement asserts the exact number of occurrences, so a changed upstream file fails loudly
instead of being half-edited. Encoding (incl. BOM) and line endings are preserved byte-for-byte.
"""
from pathlib import Path

SRC = Path(r"C:\dev\Sarus\SarusOperationPlanner")
NEW = "Sarus Operation Planner"

EDITS = [
    # Window / splash / data-folder name
    ("Program.cs", 'name = "Mission Planner";', f'name = "{NEW}";', 1),
    ("Splash.Designer.cs", 'this.Text = "Mission Planner";', f'this.Text = "{NEW}";', 1),
    ("ExtLibs/Utilities/Settings.cs", 'AppConfigName { get; set; } = "Mission Planner";',
     f'AppConfigName {{ get; set; }} = "{NEW}";', 1),
    *[(f, "<value>Mission Planner - By Michael Oborne</value>", f"<value>{NEW}</value>", 1)
      for f in ("MainV2.resx", "MainV2.zh-Hans.resx", "MainV2.zh-Hant.resx", "MainV2.zh-TW.resx")],
    ("MainV2.id-ID.resx", "<value>Mission Planner - oleh Michael Oborne</value>", f"<value>{NEW}</value>", 1),
    ("MainV2.pt.resx", "<value>Mission Planner - Por Michael Oborne</value>", f"<value>{NEW}</value>", 1),
    ("MainV2.uk.resx", "<value>Mission Planner - автор Майкл Оборн</value>", f"<value>{NEW}</value>", 2),

    # Messages shown to the user (all languages; the product name is not translated)
    ("ExtLibs/Strings/Strings.resx", "Mission Planner", NEW, 3),
    ("ExtLibs/Strings/Strings.Designer.cs", "Mission Planner", NEW, 3),
    ("ExtLibs/Strings/Strings.de-DE.resx", "Mission Planner", NEW, 1),
    ("ExtLibs/Strings/Strings.id-ID.resx", "Mission Planner", NEW, 1),
    ("ExtLibs/Strings/Strings.pt.resx", "Mission Planner", NEW, 2),
    ("ExtLibs/Strings/Strings.zh-Hans.resx", "Mission Planner", NEW, 3),
    ("ExtLibs/ArduPilot/Mavlink/MAVLinkInterface.cs", "Mission Planner waits for 2 valid heartbeat",
     f"{NEW} waits for 2 valid heartbeat", 1),
    ("ExtLibs/ArduPilot/Mavlink/MAVLinkInterface.cs", '("Mission Planner " + getAppVersion()',
     f'("{NEW} " + getAppVersion()', 1),
    ("Plugins/OpenDroneID2/OpenDroneID_Plugin.cs", "Restart Mission Planner to enable", f"Restart {NEW} to enable", 1),
    ("Plugins/TerrainMakerPlugin/TerrainMakerPlugin.cs", "Documents/Mission Planner/TerrainDat",
     f"Documents/{NEW}/TerrainDat", 1),
    ("ExtLibs/AltitudeAngelWings.Plugin/Properties/Resources.resx", "on the Mission Planner maps", f"on the {NEW} maps", 1),
    ("ExtLibs/AltitudeAngelWings.Plugin/Properties/Resources.Designer.cs", "on the Mission Planner maps",
     f"on the {NEW} maps", 1),
    ("ExtLibs/AltitudeAngelWings/Settings.cs", '"Mission Planner", s => s', f'"{NEW}", s => s', 1),
    ("ExtLibs/AltitudeAngelWings/Settings.cs", '"Mission Planner flight plan"', f'"{NEW} flight plan"', 1),
    *[(f"GCSViews/ConfigurationView/ConfigHWCompass{lang}.resx", "Mission Planner", NEW, 1)
      for lang in ("", ".az-Latn-AZ", ".fr", ".id-ID", ".pt", ".ru-KZ", ".zh-Hans")],

    # Text written into files by the app (comment / creator attribute only; formats unchanged)
    ("GCSViews/FlightPlanner.cs", '"#saved by Mission Planner "', f'"#saved by {NEW} "', 2),
    ("ExtLibs/Utilities/LogOutput.cs", '("creator", "Mission Planner")', f'("creator", "{NEW}")', 1),
]

# Validate everything first, then write, so a mismatch leaves the tree untouched.
pending = {}
for rel, old, new, expect in EDITS:
    path = SRC / rel
    data = pending.get(path) or path.read_bytes()
    old_b, new_b = old.encode("utf-8"), new.encode("utf-8")
    found = data.count(old_b)
    if found != expect:
        raise SystemExit(f"ABORT {rel}: expected {expect} x {old!r}, found {found}. Nothing written.")
    pending[path] = data.replace(old_b, new_b)

for path, data in pending.items():
    path.write_bytes(data)
    print(f"updated {path.relative_to(SRC)}")
print(f"{len(EDITS)} edits in {len(pending)} files")

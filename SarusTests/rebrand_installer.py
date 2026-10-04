"""Installer (wix/Program.cs): Sarus identity, a new UpgradeCode so installing Sarus never removes
Mission Planner, own ProgIds/registry keys, and no upload steps to upstream servers.
Driver/root-certificate behaviour is deliberately left unchanged (pending a product decision)."""
from pathlib import Path

P = Path(r"C:\dev\Sarus\SarusOperationPlanner\wix\Program.cs")
NAME = "Sarus Operation Planner"
MFR = "Sarus Aerospace Pvt Ltd"
UPGRADE = "{BB2DE14E-6D81-4F74-B41E-CDA8F7399B40}"
DIRPERM = "{249B98ED-D65F-48D2-83FE-09261ACB258E}"

EDITS = [
    ('string outputfilename = "MissionPlanner";', 'string outputfilename = "SarusOperationPlanner";', 1),
    ('<Directory Id=\\"INSTALLDIR\\" Name=\\"Mission Planner\\">', f'<Directory Id=\\"INSTALLDIR\\" Name=\\"{NAME}\\">', 1),
    ('Guid=""{525389D7-EB3C-4d77-A5F6-A285CF99437D}""', f'Guid=""{DIRPERM}""', 1),
    ('Name=""Mission Planner"" Language=""1033""', f'Name=""{NAME}"" Language=""1033""', 1),
    ('Manufacturer=""Michael Oborne"" UpgradeCode=""{625389D7-EB3C-4d77-A5F6-A285CF99437D}""',
     f'Manufacturer=""{MFR}"" UpgradeCode=""{UPGRADE}""', 1),
    ('<Package Description=""Mission Planner Installer"" Comments=""Mission Planner Installer"" Manufacturer=""Michael Oborne""',
     f'<Package Description=""{NAME} Installer"" Comments=""{NAME} Installer"" Manufacturer=""{MFR}""', 1),
    ('<Upgrade Id=""{625389D7-EB3C-4d77-A5F6-A285CF99437D}"">', f'<Upgrade Id=""{UPGRADE}"">', 1),
    ('<Directory Id=""ApplicationProgramsFolder"" Name=""Mission Planner"" />',
     f'<Directory Id=""ApplicationProgramsFolder"" Name=""{NAME}"" />', 1),
    ('Name=""Mission Planner"" Description=""Mission Planner"" Target=""[INSTALLDIR]MissionPlanner.exe""',
     f'Name=""{NAME}"" Description=""{NAME}"" Target=""[INSTALLDIR]MissionPlanner.exe""', 1),
    ('Name=""Uninstall Mission Planner"" Description=""Uninstalls My Application""',
     f'Name=""Uninstall {NAME}"" Description=""Uninstalls {NAME}""', 1),
    ('Key=""Software\\MichaelOborne\\MissionPlanner""', 'Key=""Software\\SarusAerospace\\SarusOperationPlanner""', 1),
    ('<Feature Id=""Complete"" Title=""Mission Planner"" Level=""1"">', f'<Feature Id=""Complete"" Title=""{NAME}"" Level=""1"">', 1),
    ('Value=""Launch Mission Planner""', f'Value=""Launch {NAME}""', 1),
    # Own ProgIds so Sarus does not overwrite Mission Planner's file-type registrations
    ("<ProgId Id='MissionPlanner.tlog'", "<ProgId Id='SarusOperationPlanner.tlog'", 1),
    ("<ProgId Id='MissionPlanner.dfbin'", "<ProgId Id='SarusOperationPlanner.dfbin'", 1),
    ("<ProgId Id='MissionPlanner.log'", "<ProgId Id='SarusOperationPlanner.log'", 1),
    ('Key=""MissionPlanner.tlog\\shellex', 'Key=""SarusOperationPlanner.tlog\\shellex', 2),
    ('Key=""MissionPlanner.dfbin\\shellex', 'Key=""SarusOperationPlanner.dfbin\\shellex', 2),
    ('Key=""MissionPlanner.log\\shellex', 'Key=""SarusOperationPlanner.log\\shellex', 2),
]

src = P.read_bytes()
for old, new, expect in EDITS:
    n = src.count(old.encode())
    if n != expect:
        raise SystemExit(f"ABORT: expected {expect} x {old!r}, found {n}. Nothing written.")
    src = src.replace(old.encode(), new.encode())

# Remove the release-upload steps (rsync/chmod/ln to upstream servers, interactive pauses) from create.bat
text = src.decode("utf-8-sig") if src.startswith(b"\xef\xbb\xbf") else src.decode("utf-8")
lines = text.split("\n")
drop = [i for i, l in enumerate(lines)
        if 'st.WriteLine(' in l and any(k in l for k in ('rsync.exe', 'chmod.exe', 'ln.exe', '"pause"', 'About to upload'))]
if len(drop) != 11:
    raise SystemExit(f"ABORT: expected 11 upload/pause lines in create.bat generator, found {len(drop)}. Nothing written.")
lines = [l for i, l in enumerate(lines) if i not in drop]
out = "\n".join(lines)
P.write_bytes((b"\xef\xbb\xbf" if src.startswith(b"\xef\xbb\xbf") else b"") + out.encode("utf-8"))
print(f"{len(EDITS)} edits applied, {len(drop)} upload/pause lines removed")

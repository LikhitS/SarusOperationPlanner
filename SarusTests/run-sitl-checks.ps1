# Starts a freshly wiped QuadPlane SITL, runs the GUI harness against the built ground station, prints the report.
# Usage: powershell -ExecutionPolicy Bypass -File run-sitl-checks.ps1 -Name stock|sarus
param(
    [Parameter(Mandatory)][string]$Name,
    [string]$Bin = "C:\dev\Sarus\SarusOperationPlanner\bin\Release\net461",
    [string]$AppExe = "MissionPlanner.exe",
    [ValidateSet("quadplane","copter")][string]$Vehicle = "quadplane",
    [switch]$BadLink
)
$ErrorActionPreference = 'Stop'
$root = "C:\dev\Sarus"
# one fixed simulator folder: Windows Firewall remembers its decision per program path
$run = "$root\sitl\run"
$report = "$root\tests\report-$Name.txt"

# 1. Fresh simulator (wiped eeprom) so every run starts from identical vehicle state.
Get-Process ArduPlane, ArduCopter -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 1
New-Item -ItemType Directory -Force $run | Out-Null
Get-ChildItem $run -Force | Remove-Item -Recurse -Force
$env:SARUS_VEHICLE = $Vehicle
if ($Vehicle -eq 'copter') { $simDir = 'CopterStable'; $exe = 'ArduCopter.exe'; $model = '-M+'; $parm = 'copter.parm' }
else { $simDir = 'PlaneStable'; $exe = 'ArduPlane.exe'; $model = '-Mquadplane'; $parm = 'quadplane.parm' }
Copy-Item "$root\sitl\$simDir\*" $run
Copy-Item "$root\sitl\$parm" $run
$sitl = Start-Process "$run\$exe" -ArgumentList $model,'--home','-35.363261,149.165230,584,353','-s1','-w','--serial0','tcp:0','--defaults',$parm `
    -WorkingDirectory $run -RedirectStandardOutput "$run\sitl.out" -RedirectStandardError "$run\sitl.err" -PassThru -WindowStyle Hidden
$up = $false
for ($i = 0; $i -lt 60 -and -not $up; $i++) { Start-Sleep 1; $up = [bool](Get-NetTCPConnection -LocalPort 5760 -State Listen -ErrorAction SilentlyContinue) }
if (-not $up) { throw "SITL did not open tcp 5760" }

# Optional degraded link: GCS -> relay (5800) -> SITL (5760), mode controlled by a file
$proxy = $null
if ($BadLink) {
    $ctl = "$root\tests\link-control.txt"; "ok" | Set-Content $ctl
    $proxy = Start-Process python -ArgumentList "`"$root\tests\badlink_proxy.py`"",5800,5760,"`"$ctl`"" -PassThru -WindowStyle Hidden -RedirectStandardOutput "$root\tests\proxy-$Name.log"
    Start-Sleep 2
    $env:SARUS_PORT = "5800"; $env:SARUS_LINK_CONTROL = $ctl
}

# 2. Harness sits next to the app so it loads the exact built assemblies and binding redirects.
Copy-Item "$root\tests\SarusSitlHarness\bin\Release\net472\SarusSitlHarness.exe" $Bin -Force
Copy-Item "$Bin\$AppExe.config" "$Bin\SarusSitlHarness.exe.config" -Force

$h = Start-Process "$Bin\SarusSitlHarness.exe" -ArgumentList "`"$report`"" -WorkingDirectory $Bin -PassThru
$limitMin = 15 + [int]('0' + $env:SARUS_SOAK_MINUTES)
if (-not $h.WaitForExit($limitMin * 60 * 1000)) { $h.Kill(); Add-Content $report "RESULT TIMEOUT" }

# 3. Clean up
if ($proxy) { Stop-Process -Id $proxy.Id -Force -ErrorAction SilentlyContinue }
Stop-Process -Id $sitl.Id -Force -ErrorAction SilentlyContinue
Remove-Item "$Bin\SarusSitlHarness.exe", "$Bin\SarusSitlHarness.exe.config" -ErrorAction SilentlyContinue
Get-Content $report
"EXIT " + $h.ExitCode

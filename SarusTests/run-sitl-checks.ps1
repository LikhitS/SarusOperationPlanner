# Starts a freshly wiped QuadPlane SITL, runs the GUI harness against the built ground station, prints the report.
# Usage: powershell -ExecutionPolicy Bypass -File run-sitl-checks.ps1 -Name stock|sarus
param(
    [Parameter(Mandatory)][string]$Name,
    [string]$Bin = "C:\dev\Sarus\SarusOperationPlanner\bin\Release\net461",
    [string]$AppExe = "MissionPlanner.exe",
    [ValidateSet("quadplane","copter","rover")][string]$Vehicle = "quadplane",
    [switch]$BadLink,
    # simulator build: Stable = ArduPilot SITL from firmware.ardupilot.org, Sarus = SITL built from the Sarus fork
    [ValidateSet("Stable","Sarus","Sarus463","Sarus463Lock","Sarus471","Sarus471Lock")][string]$SimSet = "Stable"
)
$ErrorActionPreference = 'Stop'
$root = "C:\dev\Sarus"
# one fixed simulator folder: Windows Firewall remembers its decision per program path
$run = "$root\sitl\run"
$report = "$root\tests\report-$Name.txt"
# A report left over from an earlier run must never be mistaken for this run's result.
Remove-Item $report, "$root\tests\report-$Name-params.csv" -Force -ErrorAction SilentlyContinue
$sitl = $null; $proxy = $null
function Fail-Run($msg) {
    if ($proxy) { Stop-Process -Id $proxy.Id -Force -ErrorAction SilentlyContinue }
    if ($sitl) { Stop-Process -Id $sitl.Id -Force -ErrorAction SilentlyContinue }
    Add-Content $report "$(Get-Date -Format 'HH:mm:ss') CHECK FAIL run-sitl-checks: $msg"
    Add-Content $report "$(Get-Date -Format 'HH:mm:ss') RESULT FAIL"
    Get-Content $report
    "EXIT 1"
    exit 1
}
trap { Fail-Run ("script error: " + $_.Exception.Message) }

# 1. Fresh simulator (wiped eeprom) so every run starts from identical vehicle state.
Get-Process ArduPlane, ArduCopter, ArduRover -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 1
New-Item -ItemType Directory -Force $run | Out-Null
Get-ChildItem $run -Force | Remove-Item -Recurse -Force
$env:SARUS_VEHICLE = $Vehicle
if ($Vehicle -eq 'copter') { $simDir = "Copter$SimSet"; $exe = 'ArduCopter.exe'; $model = '-M+'; $parm = 'copter.parm' }
elseif ($Vehicle -eq 'rover') { $simDir = "Rover$SimSet"; $exe = 'ArduRover.exe'; $model = '-Mrover'; $parm = 'rover.parm' }
else { $simDir = "Plane$SimSet"; $exe = 'ArduPlane.exe'; $model = '-Mquadplane'; $parm = 'quadplane.parm' }
Copy-Item "$root\sitl\$simDir\*" $run
Copy-Item "$root\sitl\$parm" $run
$sitl = Start-Process "$run\$exe" -ArgumentList $model,'--home','-35.363261,149.165230,584,353','-s1','-w','--serial0','tcp:0','--defaults',$parm `
    -WorkingDirectory $run -RedirectStandardOutput "$run\sitl.out" -RedirectStandardError "$run\sitl.err" -PassThru -WindowStyle Hidden
$up = $false
for ($i = 0; $i -lt 60 -and -not $up; $i++) { Start-Sleep 1; $up = [bool](Get-NetTCPConnection -LocalPort 5760 -State Listen -ErrorAction SilentlyContinue) }
if (-not $up) { Fail-Run "SITL did not open tcp 5760" }

# Optional degraded link: GCS -> relay (5800) -> SITL (5760), mode controlled by a file
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
$timedOut = $false
if (-not $h.WaitForExit($limitMin * 60 * 1000)) { $timedOut = $true; $h.Kill(); Add-Content $report "CHECK FAIL harness timeout after $limitMin minutes"; Add-Content $report "RESULT TIMEOUT" }

# 3. Clean up
if ($proxy) { Stop-Process -Id $proxy.Id -Force -ErrorAction SilentlyContinue }
Stop-Process -Id $sitl.Id -Force -ErrorAction SilentlyContinue
Remove-Item "$Bin\SarusSitlHarness.exe", "$Bin\SarusSitlHarness.exe.config" -ErrorAction SilentlyContinue
if (-not (Test-Path $report)) { Fail-Run "harness wrote no report" }
Get-Content $report
$allPass = (-not $timedOut) -and ($h.ExitCode -eq 0) -and [bool](Select-String -Path $report -Pattern 'RESULT ALL_PASS\s*$' -Quiet) -and -not (Select-String -Path $report -Pattern 'CHECK FAIL' -Quiet)
"EXIT " + $h.ExitCode
if (-not $allPass) { "RUN FAILED: result is not ALL_PASS"; exit 1 }
exit 0

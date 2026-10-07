# Acceptance test for the Sarus parameter lock with the owner's real password.
# Starts a simulated QuadPlane whose firmware carries the owner keys (the Sarus471 set, from CI's locktest build,
# holds the owner keys and the public test key), then the installed Sarus Operation Planner, or the build in
# bin\Release if it is not installed. Nothing here knows the password; you type it into Sarus yourself.
# Usage: powershell -ExecutionPolicy Bypass -File C:\dev\Sarus\tests\try-your-password.ps1
$ErrorActionPreference = 'Stop'
$root = "C:\dev\Sarus"
$sim = "$root\sitl\PlaneSarus471"
$app = "${env:ProgramFiles(x86)}\Sarus Operation Planner\MissionPlanner.exe"
if (-not (Test-Path $app)) { $app = "$root\SarusOperationPlanner\bin\Release\net461\MissionPlanner.exe" }
$run = "$root\sitl\run-owner"
if (-not (Test-Path "$sim\ArduPlane.exe")) { throw "Owner-key simulator not found in $sim" }
if (-not (Test-Path $app)) { throw "Sarus build not found: $app" }

Get-Process ArduPlane -ErrorAction SilentlyContinue | Stop-Process -Force
New-Item -ItemType Directory -Force $run | Out-Null
Copy-Item "$sim\*" $run -Force
Copy-Item "$root\sitl\quadplane.parm" $run -Force
Start-Process "$run\ArduPlane.exe" -ArgumentList '-Mquadplane','--home','-35.363261,149.165230,584,353','-s1','-w','--serial0','tcp:0','--defaults','quadplane.parm' `
    -WorkingDirectory $run -WindowStyle Minimized
Start-Sleep 3
Start-Process $app

Write-Host ""
Write-Host "In Sarus Operation Planner:"
Write-Host " 1. Top right: choose TCP, press CONNECT, host 127.0.0.1, port 5760."
Write-Host " 2. CONFIG > Full Parameter List. Change LOG_DISARMED (0 to 1) and press Write Params."
Write-Host "    Sarus asks for the admin password. Type your password."
Write-Host "    Expected: the value is written, and Messages shows 'Sarus: parameters unlocked'."
Write-Host " 3. Press 'Lock parameters' (bottom of the button column), change the value back and Write Params."
Write-Host "    Expected: the password is asked for again. Type something wrong three times:"
Write-Host "    'Wrong password, three times. Nothing was changed.'"
Write-Host " 4. Repeat step 2 with your spare password. It must unlock too."
Write-Host "Close Sarus and the ArduPlane window when you are done."

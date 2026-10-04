# Overnight unattended batch: build HEAD, every SETUP/CONFIG page, then a long soak flight.
param([switch]$Resume)   # -Resume: keep summary, skip build and pages (already done)
$ErrorActionPreference = 'Continue'
$root = "C:\dev\Sarus"
$log = "$root\tests\overnight-summary.txt"
if ($Resume) { "Resumed (sweep + soak) $(Get-Date)" | Add-Content $log } else { "Overnight batch started $(Get-Date)" | Set-Content $log }

if (-not $Resume) {
$msb = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -requires Microsoft.Component.MSBuild -find "MSBuild\Current\Bin\amd64\MSBuild.exe" | Select-Object -First 1
Push-Location "$root\SarusOperationPlanner"
& $msb -v:m -m -restore -t:Build -p:Configuration=Release MissionPlanner.sln *> "$root\build-overnight.log"
$b = $LASTEXITCODE
Pop-Location
"BUILD exit $b, head $(git -C "$root\SarusOperationPlanner" log -1 --format=%h)" | Add-Content $log
if ($b -ne 0) { "build failed - stopping" | Add-Content $log; exit 1 }

foreach ($v in 'SARUS_QUICK','SARUS_PARAM_SWEEP','SARUS_PARAM_EDITOR_CHECKS','SARUS_SCREEN_THEMES','SARUS_START_THEME','SARUS_PAGES','SARUS_SOAK_MINUTES') { Remove-Item "env:$v" -ErrorAction SilentlyContinue }

# 1. Every SETUP / CONFIG page (connected, no flight)
$env:SARUS_QUICK = "1"; $env:SARUS_PAGES = "1"
& powershell -ExecutionPolicy Bypass -File "$root\tests\run-sitl-checks.ps1" -Name "pages-quadplane" | Out-Null
Get-Content "$root\tests\report-pages-quadplane.txt" | Select-String 'CHECK FAIL|pages|RESULT' | ForEach-Object { "PAGES " + $_.Line } | Add-Content $log
Remove-Item env:SARUS_QUICK, env:SARUS_PAGES

}
# 2. Every parameter, in chunks (ArduPilot's parameter storage fills after a few hundred changes)
$env:SARUS_QUICK = "1"; $env:SARUS_PARAM_SWEEP = "1"
foreach ($skip in 0, 400, 800, 1200) {
    $env:SARUS_PARAM_SWEEP_RANGE = "$skip,400"
    & powershell -ExecutionPolicy Bypass -File "$root\tests\run-sitl-checks.ps1" -Name "sweep-quadplane-$skip" | Out-Null
    Get-Content "$root\tests\report-sweep-quadplane-$skip.txt" | Select-String 'CHECK FAIL|param sweep|RESULT' | ForEach-Object { "SWEEP " + $_.Line } | Add-Content $log
}
Remove-Item env:SARUS_QUICK, env:SARUS_PARAM_SWEEP, env:SARUS_PARAM_SWEEP_RANGE

# 3. Soak: continuous VTOL missions in one session
$env:SARUS_SOAK_MINUTES = "240"
& powershell -ExecutionPolicy Bypass -File "$root\tests\run-sitl-checks.ps1" -Name "soak-quadplane" | Out-Null
Get-Content "$root\tests\report-soak-quadplane.txt" | Select-String 'CHECK FAIL|soak|STALL|RESULT' | ForEach-Object { "SOAK " + $_.Line } | Add-Content $log
$soak = Get-Content "$root\tests\report-soak-quadplane.txt" | Select-String 'SOAK cycle'
"SOAK cycles logged: $($soak.Count); first: $($soak | Select-Object -First 1); last: $($soak | Select-Object -Last 1)" | Add-Content $log
"Overnight batch finished $(Get-Date)" | Add-Content $log
Get-Content $log

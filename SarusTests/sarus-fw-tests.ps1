# Runs the Sarus Operation Planner checks against SITL built from the Sarus firmware fork (-SimSet Sarus471).
# One line per completed step goes to sarus-fw-summary.txt; the firmware must report "... Sarus-1".
$root = "C:\dev\Sarus"
$log = "$root\tests\sarus-fw-summary.txt"
"Sarus firmware SITL tests started $(Get-Date)" | Set-Content $log
function Clear-SarusEnv { foreach ($v in 'SARUS_QUICK','SARUS_PARAM_SWEEP','SARUS_PARAM_SWEEP_RANGE','SARUS_PARAM_EDITOR_CHECKS','SARUS_SCREEN_THEMES','SARUS_START_THEME','SARUS_PAGES','SARUS_SOAK_MINUTES','SARUS_SHORTCUTS','SARUS_STRESS') { Set-Item "env:$v" "" } }
function Run($name, $vehicle) {
    & powershell -ExecutionPolicy Bypass -File "$root\tests\run-sitl-checks.ps1" -Name $name -Vehicle $vehicle -SimSet Sarus471 | Out-Null
    $r = Get-Content "$root\tests\report-$name.txt"
    $res = ($r | Select-String 'RESULT').Line
    $ver = (($r | Select-String 'CHECK INFO version') -replace '.*CHECK INFO version\s*', '')
    $sarus = if ($ver -match 'V4\.7\.1 Sarus-1') { 'firmware OK' } else { "WRONG FIRMWARE: $ver" }
    "STEP $name | $sarus | $res" | Add-Content $log
    $r | Select-String 'CHECK FAIL' | ForEach-Object { "   " + $_.Line } | Add-Content $log
}

Clear-SarusEnv; $env:SARUS_PARAM_EDITOR_CHECKS = "1"; Run "sfw-quadplane" "quadplane"
Clear-SarusEnv; Run "sfw-copter" "copter"
Clear-SarusEnv; $env:SARUS_PARAM_EDITOR_CHECKS = "1"; Run "sfw-rover" "rover"
Clear-SarusEnv; $env:SARUS_QUICK = "1"; $env:SARUS_SHORTCUTS = "1"; $env:SARUS_STRESS = "1"; Run "sfw-shortcuts" "quadplane"
foreach ($skip in 0, 400, 800, 1200) {
    Clear-SarusEnv; $env:SARUS_QUICK = "1"; $env:SARUS_PARAM_SWEEP = "1"; $env:SARUS_PARAM_SWEEP_RANGE = "$skip,400"
    Run "sfw-sweep-$skip" "quadplane"
}
"Sarus firmware SITL tests finished $(Get-Date)" | Add-Content $log
Get-Content $log

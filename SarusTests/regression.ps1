# Full regression on the current build. Writes one line per completed step to regression-summary.txt.
param([string]$Tag = "reg")
$root = "C:\dev\Sarus"
$log = "$root\tests\regression-summary.txt"
"Regression $Tag started $(Get-Date) head $(git -C "$root\SarusOperationPlanner" log -1 --format=%h) + working tree" | Set-Content $log
function Clear-SarusEnv { foreach ($v in 'SARUS_QUICK','SARUS_PARAM_SWEEP','SARUS_PARAM_SWEEP_RANGE','SARUS_PARAM_EDITOR_CHECKS','SARUS_SCREEN_THEMES','SARUS_START_THEME','SARUS_PAGES','SARUS_SOAK_MINUTES','SARUS_SHORTCUTS','SARUS_STRESS') { Set-Item "env:$v" "" } }
function Step($name, $report) {
    $r = Get-Content $report
    $res = ($r | Select-String 'RESULT').Line
    $fails = $r | Select-String 'CHECK FAIL'
    "STEP $name | $res" | Add-Content $log
    $fails | ForEach-Object { "   " + $_.Line } | Add-Content $log
}

# 1. unit tests + parity + 3 QuadPlane flights (verify-all)
Clear-SarusEnv
& powershell -ExecutionPolicy Bypass -File "$root\tests\verify-all.ps1" -Tag $Tag | Out-Null
Get-Content "$root\tests\summary-$Tag.txt" | ForEach-Object { "STEP verify | " + $_ } | Add-Content $log

# 2. param editor GUI checks
Clear-SarusEnv; $env:SARUS_QUICK = "1"; $env:SARUS_PARAM_EDITOR_CHECKS = "1"
& powershell -ExecutionPolicy Bypass -File "$root\tests\run-sitl-checks.ps1" -Name "$Tag-parameditor" | Out-Null
Step "param editor" "$root\tests\report-$Tag-parameditor.txt"

# 3. copter full flight
Clear-SarusEnv
& powershell -ExecutionPolicy Bypass -File "$root\tests\run-sitl-checks.ps1" -Name "$Tag-copter" -Vehicle copter | Out-Null
Step "copter flight" "$root\tests\report-$Tag-copter.txt"

# 4. bad link flight
Clear-SarusEnv
& powershell -ExecutionPolicy Bypass -File "$root\tests\run-sitl-checks.ps1" -Name "$Tag-badlink" -BadLink | Out-Null
Step "bad link" "$root\tests\report-$Tag-badlink.txt"

# 5. shortcuts + stress
Clear-SarusEnv; $env:SARUS_QUICK = "1"; $env:SARUS_SHORTCUTS = "1"; $env:SARUS_STRESS = "1"
& powershell -ExecutionPolicy Bypass -File "$root\tests\run-sitl-checks.ps1" -Name "$Tag-shortcuts" | Out-Null
Step "shortcuts + stress" "$root\tests\report-$Tag-shortcuts.txt"

# 6. QuadPlane parameter sweep
foreach ($skip in 0, 400, 800, 1200) {
    Clear-SarusEnv; $env:SARUS_QUICK = "1"; $env:SARUS_PARAM_SWEEP = "1"; $env:SARUS_PARAM_SWEEP_RANGE = "$skip,400"
    & powershell -ExecutionPolicy Bypass -File "$root\tests\run-sitl-checks.ps1" -Name "$Tag-sweep-$skip" | Out-Null
    Step "sweep part $(($skip / 400) + 1)/4" "$root\tests\report-$Tag-sweep-$skip.txt"
}
"Regression finished $(Get-Date)" | Add-Content $log
Get-Content $log

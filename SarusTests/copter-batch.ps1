# Copter: every SETUP/CONFIG page, then every parameter in chunks.
$root = "C:\dev\Sarus"
$log = "$root\tests\copter-summary.txt"
"Copter batch started $(Get-Date)" | Set-Content $log
$env:SARUS_QUICK = "1"; $env:SARUS_PAGES = "1"
& powershell -ExecutionPolicy Bypass -File "$root\tests\run-sitl-checks.ps1" -Name "pages-copter" -Vehicle copter | Out-Null
Get-Content "$root\tests\report-pages-copter.txt" | Select-String 'CHECK FAIL|pages|RESULT' | ForEach-Object { "PAGES " + $_.Line } | Add-Content $log
$env:SARUS_PAGES = ""
$env:SARUS_PARAM_SWEEP = "1"
foreach ($skip in 0, 400, 800, 1200) {
    $env:SARUS_PARAM_SWEEP_RANGE = "$skip,400"
    & powershell -ExecutionPolicy Bypass -File "$root\tests\run-sitl-checks.ps1" -Name "sweep-copter-$skip" -Vehicle copter | Out-Null
    Get-Content "$root\tests\report-sweep-copter-$skip.txt" | Select-String 'CHECK FAIL|param sweep|RESULT' | ForEach-Object { "SWEEP " + $_.Line } | Add-Content $log
}
"Copter batch finished $(Get-Date)" | Add-Content $log
Get-Content $log

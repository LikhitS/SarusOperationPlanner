# Long soak flight only (appends to the overnight summary).
param([int]$Minutes = 240)
$root = "C:\dev\Sarus"
$log = "$root\tests\overnight-summary.txt"
$rep = "$root\tests\report-soak-quadplane.txt"
# progress.ps1 measures soak time from the report's creation time: start from a new file
if (Test-Path -LiteralPath $rep) { [IO.File]::Delete($rep) }
"Soak (restarted with mission restart fix) $(Get-Date)" | Add-Content $log
$env:SARUS_SOAK_MINUTES = "$Minutes"
& powershell -ExecutionPolicy Bypass -File "$root\tests\run-sitl-checks.ps1" -Name "soak-quadplane" | Out-Null
Get-Content $rep | Select-String 'CHECK FAIL|soak|STALL|RESULT' | ForEach-Object { "SOAK " + $_.Line } | Add-Content $log
$cycles = Get-Content $rep | Select-String 'SOAK cycle'
"SOAK cycles logged: $($cycles.Count); last: $($cycles | Select-Object -Last 1)" | Add-Content $log
"Overnight batch finished $(Get-Date)" | Add-Content $log
Get-Content $log | Select-Object -Last 8

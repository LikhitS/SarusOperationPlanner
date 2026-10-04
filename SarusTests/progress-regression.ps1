# Progress lines for regression.ps1 (11 steps). Read-only.
$total = 11; $last = ''
while ($true) {
    $s = if (Test-Path C:\dev\Sarus\tests\regression-summary.txt) { Get-Content C:\dev\Sarus\tests\regression-summary.txt } else { @() }
    $verify = ($s | Select-String '^STEP verify \| FLIGHT').Count     # 3 flights
    $unit = [bool]($s -match '^STEP verify \| UNIT')
    $others = ($s | Select-String '^STEP (?!verify)').Count
    $done = $verify + $others + $(if ($unit) { 1 } else { 0 })
    $fails = ($s | Select-String 'CHECK FAIL|FAIL=[1-9]|unexpected failures: (?!none)|RESULT \d+ FAILURE').Count
    $pct = [math]::Min(100, [math]::Round(100 * $done / $total))
    $bar = ('#' * [math]::Floor($pct / 5)).PadRight(20, '-')
    $lastStep = ($s | Select-String '^STEP' | Select-Object -Last 1).Line
    $line = "[$bar] $pct% | $done/$total steps | failures: $fails | last: $lastStep"
    if ($line -ne $last) { $line; $last = $line }
    if ($s -match 'finished') { break }
    Start-Sleep 20
}

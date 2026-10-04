# Progress lines for the Copter batch (pages + 4 sweep chunks). Read-only.
$last = ''
while ($true) {
    $s = if (Test-Path C:\dev\Sarus\tests\copter-summary.txt) { Get-Content C:\dev\Sarus\tests\copter-summary.txt } else { @() }
    $pages = [bool]($s -match '^PAGES .*RESULT')
    $chunks = ($s | Select-String '^SWEEP .*RESULT').Count
    $fails = ($s | Select-String 'CHECK FAIL').Count
    $done = $chunks + $(if ($pages) { 1 } else { 0 })
    $pct = [math]::Round(100 * $done / 5)
    $bar = ('#' * [math]::Floor($pct / 5)).PadRight(20, '-')
    $line = "[$bar] $pct% | Copter: pages $(if ($pages) { 'done' } else { 'running' }), parameter sweep $chunks/4 | failures: $fails"
    if ($line -ne $last) { $line; $last = $line }
    if ($s -match 'finished') { break }
    Start-Sleep 20
}

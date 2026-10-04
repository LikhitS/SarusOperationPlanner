# Emits one progress line for the overnight batch: on every stage change and at least every 15 minutes.
# Exits when the batch finishes. Read-only.
$root = "C:\dev\Sarus\tests"
$sum = "$root\overnight-summary.txt"
$weights = @{ build = 2; pages = 5; sweep = 24; soak = 242 }
$total = ($weights.Values | Measure-Object -Sum).Sum
$lastStage = ""; $lastEmit = [DateTime]::MinValue

while ($true) {
    $s = if (Test-Path $sum) { Get-Content $sum } else { @() }
    $done = 0.0; $stage = "starting"
    $fails = ($s | Select-String 'CHECK FAIL').Count
    if ($s -match '^BUILD') { $done += $weights.build; $stage = "all-pages test" }
    if ($s -match '^PAGES') { $done += $weights.pages; $stage = "parameter sweep 1/4" }
    $chunks = ($s | Select-String '^SWEEP .*RESULT').Count
    if ($chunks -gt 0) { $done += $weights.sweep * $chunks / 4; $stage = if ($chunks -lt 4) { "parameter sweep $($chunks + 1)/4" } else { "4-hour flight test" } }
    $cycles = ""
    $soakRep = "$root\report-soak-quadplane.txt"
    if ($chunks -ge 4 -and (Test-Path $soakRep)) {
        $r = Get-Content $soakRep
        $start = (Get-Item $soakRep).CreationTime
        $mins = [math]::Min(242, ((Get-Date) - $start).TotalMinutes)
        $done += $mins
        $ok = ($r | Select-String 'SOAK cycle .* ok').Count
        $bad = ($r | Select-String 'SOAK cycle .* FAILED').Count
        $mem = (($r | Select-String 'SOAK cycle' | Select-Object -Last 1).Line -replace '.*mem (\d+) MB.*', '$1')
        $cycles = " | flights ok $ok, failed $bad" + $(if ($mem -match '^\d+$') { ", app memory $mem MB" } else { "" })
        $stage = "4-hour flight test {0:0}h{1:00}m / 4h" -f [math]::Floor($mins / 60), ($mins % 60)
    }
    if ($s -match 'finished') { $done = $total; $stage = "finished" }
    $pct = [math]::Min(100, [math]::Round(100 * $done / $total))
    $bar = ('#' * [math]::Floor($pct / 5)).PadRight(20, '-')
    $stageKey = ($stage -replace '\d+h\d+m.*', '')
    if ($stageKey -ne $lastStage -or ((Get-Date) - $lastEmit).TotalMinutes -ge 15) {
        "[$bar] $pct% | $stage$cycles | failures so far: $fails | $(Get-Date -Format HH:mm)"
        $lastStage = $stageKey; $lastEmit = Get-Date
    }
    if ($stage -eq "finished") { break }
    Start-Sleep 60
}

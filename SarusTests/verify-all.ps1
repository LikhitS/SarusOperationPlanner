# Full verification of the current build against the recorded stock baselines.
# Usage: powershell -ExecutionPolicy Bypass -File verify-all.ps1 -Tag sarus1
param([Parameter(Mandatory)][string]$Tag, [int]$FlightRuns = 3)
$root = "C:\dev\Sarus"
$src = "$root\SarusOperationPlanner"
$sum = New-Object System.Collections.Generic.List[string]

# 1. Upstream unit tests: must match the stock baseline (31 pass, the same 7 known failures)
Push-Location $src
dotnet test MissionPlannerTests\MissionPlannerTests.csproj -c Release *> "$root\tests\unit-$Tag.log"
Pop-Location
$u = Get-Content "$root\tests\unit-$Tag.log"
$failed = $u | Select-String '^\s+Failed (\w+)' | ForEach-Object { $_.Matches[0].Groups[1].Value } | Sort-Object
$known = 'DetectBoardTest3','DetectBoardTest4','DetectBoardTest5','DetectBoardTest6','DetectBoardTest8','DetectBoardTestMany','getFilefromNetTest' | Sort-Object
$summary = ($u | Select-String 'Passed!|Failed!' | Select-Object -Last 1).Line
$sum.Add("UNIT  $summary")
$unexpected = $failed | Where-Object { $_ -notin $known }
$sum.Add("UNIT  unexpected failures: " + $(if ($unexpected) { $unexpected -join ',' } else { 'none' }))

# 2. Feature / shortcut parity inventory vs stock
& powershell -ExecutionPolicy Bypass -File "$root\tests\feature-inventory.ps1" -Out "$root\tests\inventory-$Tag.tsv" | Out-Null
$diff = Compare-Object (Get-Content "$root\tests\inventory-stock.tsv") (Get-Content "$root\tests\inventory-$Tag.tsv")
$diff | ForEach-Object { $_.SideIndicator + " " + $_.InputObject } | Set-Content "$root\tests\inventory-diff-$Tag.txt"
$removed = ($diff | Where-Object SideIndicator -eq '<=').Count
$added = ($diff | Where-Object SideIndicator -eq '=>').Count
$keyChanges = ($diff | Where-Object { $_.InputObject -like 'KEY*' -or $_.InputObject -like 'SCREEN*' -or $_.InputObject -like 'MENU*' -or $_.InputObject -like 'CTRL*' }).Count
$sum.Add("PARITY  lines removed $removed, added $added; shortcut/screen/menu/control changes: $keyChanges")

# 3. SITL QuadPlane flight checks
for ($i = 1; $i -le $FlightRuns; $i++) {
    $out = & powershell -ExecutionPolicy Bypass -File "$root\tests\run-sitl-checks.ps1" -Name "$Tag-$i"
    $r = Get-Content "$root\tests\report-$Tag-$i.txt"
    $sum.Add(("FLIGHT run {0}: PASS={1} FAIL={2} {3}" -f $i, ($r | Select-String 'CHECK PASS').Count,
        ($r | Select-String 'CHECK FAIL').Count, ($r | Select-String '^.{9}RESULT').Line))
    $r | Select-String 'CHECK FAIL' | ForEach-Object { $sum.Add("   " + $_.Line) }
}
$sum | Set-Content "$root\tests\summary-$Tag.txt"
$sum

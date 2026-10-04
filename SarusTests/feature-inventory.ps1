# Builds a parity inventory of keyboard shortcuts, menus, screens and UI controls from the source tree.
# Run on stock code once (baseline), then after every change; any diff must be reviewed before release.
# Usage: powershell -ExecutionPolicy Bypass -File feature-inventory.ps1 -Out <file>
param(
    [string]$Src = "C:\dev\Sarus\SarusOperationPlanner",
    [Parameter(Mandatory)][string]$Out
)

# SarusTests / SarusBranding hold the test tools and logo sources: in the repo but not part of the built app
$files = Get-ChildItem $Src -Recurse -Include *.cs -File |
    Where-Object { $_.FullName -notmatch '\\ExtLibs\\mono\\|\\obj\\|\\bin\\|\\SarusTests\\|\\SarusBranding\\' }

$lines = New-Object System.Collections.Generic.List[string]
foreach ($f in $files) {
    $rel = $f.FullName.Substring($Src.Length + 1)
    $n = 0
    foreach ($line in [IO.File]::ReadAllLines($f.FullName)) {
        $n++
        $t = $line.Trim()
        # Keyboard shortcuts and key handling
        if ($t -match 'Keys\.(Control|Alt|Shift)\s*\|\s*Keys\.\w+|keyData\s*==|ShortcutKeys\s*=|ShortcutKeyDisplayString|ProcessCmdKey|KeyCode\s*==\s*Keys\.') {
            $lines.Add("KEY`t$rel`t$t")
        }
        # Menu / toolstrip / button / tab captions defined in designers
        elseif ($f.Name -like '*.Designer.cs' -and $t -match '^this\.(\w+)\.(Text|ToolTipText)\s*=\s*(.+);$') {
            $lines.Add("UI`t$rel`t$($matches[1]).$($matches[2])")
        }
        # Controls that exist on each screen
        elseif ($f.Name -like '*.Designer.cs' -and $t -match '^this\.(\w+)\s*=\s*new\s+([\w\.]+)\(') {
            $lines.Add("CTRL`t$rel`t$($matches[1]) : $($matches[2])")
        }
        # Screens and pages registered in the app
        elseif ($t -match 'AddScreen\(|new MainSwitcher\.Screen\(|AddBackstageViewPage\(') {
            $lines.Add("SCREEN`t$rel`t$t")
        }
        # Context-menu / action items built in code
        elseif ($t -match 'new ToolStripMenuItem\(|\.DropDownItems\.Add\(|\.Items\.Add\("') {
            $lines.Add("MENU`t$rel`t$t")
        }
    }
}
# Captions and shortcuts stored in the default-language resource files (localised .xx.resx are skipped)
$resx = Get-ChildItem $Src -Recurse -Filter *.resx -File |
    Where-Object { $_.FullName -notmatch '\\ExtLibs\\mono\\|\\obj\\|\\bin\\|\\SarusTests\\|\\SarusBranding\\' -and $_.Name -notmatch '\.[a-z]{2}(-[A-Za-z]{2,4})?\.resx$' }
foreach ($f in $resx) {
    $rel = $f.FullName.Substring($Src.Length + 1)
    try { [xml]$x = [IO.File]::ReadAllText($f.FullName) } catch { $lines.Add("RESXERR`t$rel"); continue }
    foreach ($d in $x.root.data) {
        if ($d.name -match '\.(Text|ToolTipText|ShortcutKeys|ShortcutKeyDisplayString|HeaderText)$' -and $d.value) {
            $kind = if ($d.name -match 'Shortcut') { 'KEY' } else { 'UI' }
            $lines.Add("$kind`t$rel`t$($d.name) = $(($d.value -replace '\s+', ' ').Trim())")
        }
    }
}
$sorted = $lines | Sort-Object
[IO.File]::WriteAllLines($Out, [string[]]$sorted)
$sorted | ForEach-Object { ($_ -split "`t")[0] } | Group-Object | ForEach-Object { "{0,-7} {1}" -f $_.Name, $_.Count }

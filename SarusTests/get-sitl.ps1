# Downloads ArduPilot's official Windows (Cygwin) SITL builds, mirroring GCSViews/SITL.cs.
$ErrorActionPreference = 'Stop'
$base = 'https://firmware.ardupilot.org/Tools/MissionPlanner/sitl/'
$dlls = 'cygatomic-1.dll','cyggcc_s-1.dll','cyggcc_s-seh-1.dll','cyggomp-1.dll','cygiconv-2.dll',
        'cygintl-8.dll','cygquadmath-0.dll','cygssp-0.dll','cygstdc++-6.dll','cygwin1.dll'

foreach ($v in @(@{dir='PlaneStable'; exe='ArduPlane'}, @{dir='CopterStable'; exe='ArduCopter'})) {
    $out = Join-Path $PSScriptRoot $v.dir
    New-Item -ItemType Directory -Force $out | Out-Null
    Invoke-WebRequest "$base$($v.dir)/$($v.exe).elf" -OutFile "$out\$($v.exe).exe" -UseBasicParsing
    foreach ($d in $dlls) {
        try { Invoke-WebRequest "$base$($v.dir)/$d" -OutFile "$out\$d" -UseBasicParsing } catch { Write-Host "optional dll missing: $($v.dir)/$d" }
    }
}
$autotest = 'https://raw.githubusercontent.com/ArduPilot/ardupilot/master/Tools/autotest/'
foreach ($p in 'models/plane.parm','default_params/copter.parm','default_params/quadplane.parm') {
    Invoke-WebRequest "$autotest$p" -OutFile (Join-Path $PSScriptRoot (Split-Path $p -Leaf)) -UseBasicParsing
}
Get-ChildItem $PSScriptRoot -Recurse -File | Select-Object FullName, Length

#!/bin/bash
# Sarus-2 on ArduPilot 4.6.3: lock, limits + alarm, copter and rover flights
R='C:/dev/Sarus/tests/run-sitl-checks.ps1'
report() {
    n=$1
    f=/c/dev/Sarus/tests/report-$n.txt
    echo "== $n: $(grep -c 'CHECK PASS' $f) pass; $(grep RESULT $f)"
    grep "CHECK FAIL" $f | cut -c1-200
}
SARUS_QUICK=1 SARUS_LOCKTEST=1 powershell -ExecutionPolicy Bypass -File "$R" -Name s463-lock -SimSet Sarus463Lock > /dev/null 2>&1; report s463-lock
SARUS_ALARMTEST=1 powershell -ExecutionPolicy Bypass -File "$R" -Name s463-alarm -SimSet Sarus463 > /dev/null 2>&1; report s463-alarm
powershell -ExecutionPolicy Bypass -File "$R" -Name s463-copter -SimSet Sarus463 -Vehicle copter > /dev/null 2>&1; report s463-copter
powershell -ExecutionPolicy Bypass -File "$R" -Name s463-rover -SimSet Sarus463 -Vehicle rover > /dev/null 2>&1; report s463-rover
echo BATCH DONE

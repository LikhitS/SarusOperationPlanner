#!/bin/bash
# Sarus-2 on ArduPilot 4.6.3: lock, limits + alarm, copter and rover flights
R='C:/dev/Sarus/tests/run-sitl-checks.ps1'
FAILED=0
# Deletes the old report first (a stale one must not pass as fresh), runs the step, keeps the runner output
# in run-<name>.log, and counts a missing or non-ALL_PASS result as FAIL.
run() {
    n=$1; shift
    f=/c/dev/Sarus/tests/report-$n.txt
    rm -f "$f" "/c/dev/Sarus/tests/report-$n-params.csv"
    powershell -ExecutionPolicy Bypass -File "$R" -Name "$n" "$@" > "/c/dev/Sarus/tests/run-$n.log" 2>&1
    rc=$?
    res=$(grep RESULT $f 2>/dev/null)
    if [ $rc -ne 0 ] || ! echo "$res" | grep -q 'RESULT ALL_PASS'; then
        FAILED=$((FAILED+1)); res="FAIL ($res, runner exit $rc)"
    fi
    echo "== $n: $(grep -c 'CHECK PASS' $f 2>/dev/null) pass; $res"
    grep "CHECK FAIL" $f 2>/dev/null | cut -c1-200
}
SARUS_QUICK=1 SARUS_LOCKTEST=1 run s463-lock -SimSet Sarus463Lock
SARUS_ALARMTEST=1 run s463-alarm -SimSet Sarus463
run s463-copter -SimSet Sarus463 -Vehicle copter
run s463-rover -SimSet Sarus463 -Vehicle rover
echo "BATCH DONE, $FAILED step(s) failed"
[ "$FAILED" -eq 0 ]

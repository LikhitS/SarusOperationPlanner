#!/bin/bash
# Full regression of the Sarus v2 build against one simulator set, step by step with the same runner
# regression.ps1 uses, plus the v2 checks (parameter lock, airframe limits, in-flight alarm, every page).
#   regression-v2.sh <tag> <simset> [locksimset]
#   simset: Stable (ArduPilot), Sarus (4.7.1-S1), Sarus463, Sarus463Lock, Sarus471, Sarus471Lock
TAG=$1; SIM=$2; LOCKSIM=$3
R='C:/dev/Sarus/tests/run-sitl-checks.ps1'
SUM=/c/dev/Sarus/tests/regression-$TAG.txt
echo "Regression $TAG on $SIM started $(date)" > $SUM
step() {
    name=$1; n=$2
    f=/c/dev/Sarus/tests/report-$n.txt
    echo "STEP $name | $(grep -c 'CHECK PASS' $f 2>/dev/null) pass | $(grep RESULT $f 2>/dev/null)" | tee -a $SUM
    grep "CHECK FAIL" $f 2>/dev/null | cut -c1-200 | sed 's/^/    /' | tee -a $SUM
}
ps() { powershell -ExecutionPolicy Bypass -File "$R" "$@" > /dev/null 2>&1; }

SARUS_QUICK=1 SARUS_PARAM_EDITOR_CHECKS=1 ps -Name $TAG-parameditor -SimSet $SIM; step "param editor" $TAG-parameditor
ps -Name $TAG-quadplane -SimSet $SIM; step "quadplane flight" $TAG-quadplane
ps -Name $TAG-copter -SimSet $SIM -Vehicle copter; step "copter flight" $TAG-copter
SARUS_PARAM_EDITOR_CHECKS=1 ps -Name $TAG-rover -SimSet $SIM -Vehicle rover; step "rover drive" $TAG-rover
SARUS_ALARMTEST=1 ps -Name $TAG-alarm -SimSet $SIM; step "limits + alarm flight" $TAG-alarm
ps -Name $TAG-badlink -SimSet $SIM -BadLink; step "bad link flight" $TAG-badlink
SARUS_QUICK=1 SARUS_SHORTCUTS=1 SARUS_STRESS=1 ps -Name $TAG-shortcuts -SimSet $SIM; step "shortcuts + stress" $TAG-shortcuts
SARUS_QUICK=1 SARUS_PAGES=1 ps -Name $TAG-pages-quadplane -SimSet $SIM; step "every page, quadplane" $TAG-pages-quadplane
SARUS_QUICK=1 SARUS_PAGES=1 ps -Name $TAG-pages-copter -SimSet $SIM -Vehicle copter; step "every page, copter" $TAG-pages-copter
for skip in 0 400 800 1200; do
    SARUS_QUICK=1 SARUS_PARAM_SWEEP=1 SARUS_PARAM_SWEEP_RANGE="$skip,400" ps -Name $TAG-sweep-$skip -SimSet $SIM
    step "quadplane sweep $skip" $TAG-sweep-$skip
done
for skip in 0 400 800; do
    SARUS_QUICK=1 SARUS_PARAM_SWEEP=1 SARUS_PARAM_SWEEP_RANGE="$skip,400" ps -Name $TAG-rover-sweep-$skip -SimSet $SIM -Vehicle rover
    step "rover sweep $skip" $TAG-rover-sweep-$skip
done
if [ -n "$LOCKSIM" ]; then
    SARUS_QUICK=1 SARUS_LOCKTEST=1 ps -Name $TAG-lock -SimSet $LOCKSIM; step "parameter lock" $TAG-lock
    SARUS_QUICK=1 SARUS_LOCKTEST=1 ps -Name $TAG-lock-copter -SimSet $LOCKSIM -Vehicle copter; step "parameter lock, copter" $TAG-lock-copter
fi
echo "Regression $TAG finished $(date)" | tee -a $SUM
echo "REGRESSION DONE"

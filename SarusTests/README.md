# Sarus test tooling

Automated checks used to verify Sarus Operation Planner against ArduPilot SITL (simulated aircraft).
Scripts currently assume the layout `C:\dev\Sarus\` (repo at `C:\dev\Sarus\SarusOperationPlanner`,
tooling copied to `C:\dev\Sarus\tests`, simulators in `C:\dev\Sarus\sitl`).

## Setup
1. Build the solution (Release).
2. `get-sitl.ps1` downloads ArduPilot's Windows SITL builds (Plane, Copter) and default parameters.
3. `dotnet build -c Release` in `SarusSitlHarness` and `StackDump`.

## Main entry points
| Script | What it does |
|---|---|
| `run-sitl-checks.ps1 -Name X [-Vehicle quadplane\|copter] [-BadLink]` | Fresh SITL + the GUI harness against the built app; report in `report-X.txt` |
| `verify-all.ps1 -Tag X` | Unit tests, feature/shortcut parity vs `inventory-stock.tsv`, 3 SITL flights |
| `regression.ps1 -Tag X` | Full regression: verify-all, param editor, Copter, bad link, shortcuts + concurrency stress, full parameter sweep |
| `overnight.ps1`, `soak.ps1 -Minutes N` | All pages, chunked parameter sweep, long continuous-flight soak |
| `progress*.ps1` | Progress lines for the long batches |

## Harness switches (environment variables)
`SARUS_QUICK` (skip the flight), `SARUS_PARAM_SWEEP` + `SARUS_PARAM_SWEEP_RANGE=skip,take`, `SARUS_PARAM_EDITOR_CHECKS`,
`SARUS_PAGES`, `SARUS_SHORTCUTS`, `SARUS_STRESS`, `SARUS_SOAK_MINUTES`, `SARUS_SCREEN_THEMES`, `SARUS_START_THEME`.

## Diagnostics
- The harness flags any telemetry stall > 4 s and takes a managed stack snapshot (`StackDump`).
- `tlog_timeline.py` / `tlog_stall.py` decode telemetry logs around an incident.
- `badlink_proxy.py` degrades the link (loss, delay, blackout) between the app and SITL.

`rebrand_*.py` are the reviewed, count-checked edit scripts used for the user-visible rebrand.

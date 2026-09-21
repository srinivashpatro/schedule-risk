# ScheduleRisk - schedule risk analysis for Primavera P6 (XER)

Schedule risk analysis for P6 schedules, as a browser app plus a command-line tool:
read a P6 XER file, recalculate it with a CPM engine that follows P6's rules,
check the schedule's health, apply a risk model, run a Monte Carlo simulation,
and report P-dates, criticality, sensitivity and risk rankings.

Status: **v0.2 - engine core, command line, and a browser app (Blazor WebAssembly).**
The browser app runs the whole engine inside the user's browser: XER files are never uploaded,
and the site can be hosted as plain static files.

## Build and test (Windows)

1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
2. Open a terminal in this folder and run `build-and-test.cmd`
   (or open `ScheduleRisk.sln` in Visual Studio 2022 and use Test Explorer).

> The C# code was written in a sandbox **without a .NET compiler**. It was checked against a
> Python twin of the engine that *was* run and tested, but the first `dotnet build` may still
> report compile errors. Paste them back to Claude and they will be fixed.

## Browser app

    run-web.cmd        # start locally, then open the address it prints
    publish-web.cmd    # static site in publish\wwwroot - copy to any web host

Three steps in one page: **Schedule** (open XER, P6 check, health checks, activity grid) ->
**Risk model** (uncertainty, risk register with mitigation, drivers, correlation; open/save
model JSON) -> **Simulate & results** (progress, S-curve, P-dates, tornado, criticality,
milestones; download HTML report and CSVs).

The first load downloads the .NET runtime (~10-15 MB, cached afterwards). Simulations run on
one CPU core in the browser, so they are much slower than the command line (speed not yet
measured). For large schedules, turn on ahead-of-time compilation: run
`dotnet workload install wasm-tools` once and add `<RunAOTCompilation>true</RunAOTCompilation>`
to `ScheduleRisk.Web.csproj` before publishing (bigger download, several times faster).
If you host it in a sub-folder (e.g. GitHub Pages), change `<base href="/">` in
`src/ScheduleRisk.Web/wwwroot/index.html` to that folder.

## Command line

```
sra info      project.xer
sra validate  project.xer                      # DCMA-style schedule health checks
sra verify    project.xer                      # our CPM dates vs the dates P6 saved in the file
sra cpm       project.xer --csv dates.csv
sra simulate  project.xer --risk model.json --out results   # pre + post mitigation, HTML report
```

From source: `dotnet run --project src\ScheduleRisk.Cli -c Release -- <command> ...`

`simulate` writes `report.html` (self-contained, open in any browser), `summary.pre.json`,
`summary.post.json`, `activities.csv`, `risks.csv` and `iterations.csv`.

The risk model format is described in [docs/RISK_MODEL.md](docs/RISK_MODEL.md).

## The P6 check (the most important test)

P6 writes its own early/late dates and total float into every XER it exports. `sra verify`
recalculates the schedule and compares every date, in working time, with what P6 saved. A schedule
that verifies cleanly is one whose risk results you can trust. Run it on your real XER files
first; any differences point at a P6 rule the engine does not yet follow.

## Layout

| Path | What |
| --- | --- |
| `src/ScheduleRisk.Core` | Engine library (no dependencies): `Xer`, `Calendars`, `Model`, `Cpm`, `Analysis`, `Risk`, `Simulation`, `Reporting` |
| `src/ScheduleRisk.Cli` | `sra` command-line tool |
| `src/ScheduleRisk.Web` | Blazor WebAssembly browser app (`Components/` holds the three panels and editors) |
| `tests/ScheduleRisk.Tests` | xUnit: hand-calculated CPM cases, parity with the Python reference, analytic simulation checks |
| `reference/python` | Python twin of the engine, the executable specification (stdlib only; `python -m unittest discover -s tests`) |
| `testdata` | Hand-built and synthetic XER files, an example risk model, golden results from the reference engine |

## What the engine does

**Scheduling (P6 rules):** FS/SS/FF/SF relationships with lags on the predecessor, successor,
24-hour or project calendar (per the project setting); per-activity calendars with holidays and
exceptions parsed from P6's `clndr_data`; data date; retained logic or progress override;
constraints start/finish on, on-or-after, on-or-before, mandatory start/finish; start and finish
milestones; LOE and WBS summary activities (non-driving); predecessors in other projects held at
their P6 dates; must-finish-by; finish, start or smallest float.

**Not yet:** ALAP constraints, expected finish, resource levelling, "make open-ended activities
critical", P6 XML / MS Project import (all flagged by `validate` where relevant).

**Risk:** three-point uncertainty (triangle, Beta-PERT, uniform) in percent or days; risk register
with probability, impact and mitigated values; risk drivers; correlation groups; Latin Hypercube
sampling; seed-reproducible results independent of CPU count; batch convergence.

**Outputs:** P5-P95 finish dates, probability of meeting the deterministic date, milestone P-dates,
criticality index, duration sensitivity (Spearman), cruciality, risk and driver tornado data,
pre/post-mitigation comparison.

## Numbers from the reference engine (synthetic 500-activity schedule, 500 iterations)

Deterministic finish 13-Sep-2028; P50 15-Mar-2029, P80 29-Jun-2029 before mitigation;
P80 08-Mar-2029 after mitigation. The C# tests assert the same results.

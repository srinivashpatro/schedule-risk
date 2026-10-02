# Project Risk Analysis - schedule risk analysis for Primavera P6 (XER)

Schedule risk analysis for P6 schedules, as a browser app plus a command-line tool:
read a P6 XER file, recalculate it with a CPM engine that follows P6's rules,
check the schedule's health, apply a risk model, run a Monte Carlo simulation,
and report P-dates, criticality, sensitivity and risk rankings.

Status: **v0.8 - engine core, command line, and a browser app (Blazor WebAssembly), laid out as a
seven-step risk workflow (qualitative risk register, kept as Excel or JSON -> Promote -> P6 and DCMA
schedule check -> model -> results with tornado and cost-benefit -> reports with a table of contents).**
Changes by release are in [CHANGELOG.md](CHANGELOG.md).
The browser app runs the whole engine inside the user's browser: XER files are never uploaded,
and the site can be hosted as plain static files. Try it at
<https://srinivashpatro.github.io/schedule-risk/>. Each release also stays online at its own address:

| Release | Address | Built from |
| --- | --- | --- |
| 0.8.0 | <https://srinivashpatro.github.io/schedule-risk/v0.8/> | tag `v0.8.0` |
| 0.7.0 | <https://srinivashpatro.github.io/schedule-risk/v0.7/> | tag `v0.7.0` |
| 0.6.0 | <https://srinivashpatro.github.io/schedule-risk/v0.6/> | tag `V0.6.0` |
| 0.4.0 | <https://srinivashpatro.github.io/schedule-risk/v0.4/> | tag `v0.4.0` |
| 0.3.0 | <https://srinivashpatro.github.io/schedule-risk/v0.3/> | branch `release/0.3` |

A second copy of the site can run on Cloudflare Pages behind Cloudflare Access, open only to invited
email addresses that sign in with a one-time PIN; the app itself is the same and still uploads nothing
(set-up and privacy notes in [docs/HOSTING.md](docs/HOSTING.md)).

## Build and test (Windows)

1. Install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
2. Open a terminal in this folder and run `build-and-test.cmd`
   (or open `ScheduleRisk.sln` in Visual Studio 2022 and use Test Explorer).

GitHub Actions runs the same build and tests, plus the Python reference tests, on every push to
`main`, and publishes the browser app to GitHub Pages when they pass, and to Cloudflare Pages once
that is set up (see [.github/workflows/build-and-deploy.yml](.github/workflows/build-and-deploy.yml)
and [docs/HOSTING.md](docs/HOSTING.md)).

## Browser app

    run-web.cmd        # start locally, then open the address it prints
    publish-web.cmd    # static site in publish\wwwroot - copy to any web host
    publish-offline.cmd  # offline copy in publish\ScheduleRisk-offline.zip

To use the app with no internet connection, unzip the offline copy (built by `publish-offline.cmd`, or
downloaded as the `ScheduleRisk-offline` artifact of a GitHub Actions run) and double-click
`Start-Offline.cmd`. It serves the app from your own computer at `http://127.0.0.1:5180/` using
Windows PowerShell, with nothing to install. Opening `index.html` directly doesn't work, because
browsers won't start a WebAssembly app from a `file://` address.

The quantitative steps: **04 Schedule check** (open or drop an XER, or use the sample project; the
engine check against P6, the P6 Check Schedule and DCMA 14-Point health checks with every flagged
activity, a P6-style Gantt chart or the activity table) -> **05 Model** (the risks promoted from the register,
duration uncertainty, drivers and correlation, each applied to activities picked from a searchable
list by ID or name, or by WBS, code or critical path; simulation settings; open/save model JSON; run) ->
**06 Results** (a Summary panel with the key outputs: P50, P80 and any confidence level with their
contingency, mean, median and spread, top risk drivers, critical and near-critical activities; under
it, what those results mean in plain words, with a glossary; then the finish distribution as
one combined chart (histogram of finish dates per day or per week on the frequency axis, S-curve on
the cumulative percent axis), confidence table, the risk tornado before and after mitigation, criticality,
milestones, and on request the cost-benefit of each response from paired runs) -> **07 Review** (the
report as PDF, Word or PowerPoint, as an HTML page, or as CSVs).

The app is laid out as a seven-step risk workflow in a column beside the page: 01 Setup, 02
Identify, 03 Assess, Promote, 04 Schedule check, 05 Model, 06 Results and 07 Review (the report
formats). Step 01 sets up the risk register's probability-severity matrix (5 x 5 by default, rated
Red, Amber or Green cell by cell, all editable) and saves the register as an Excel workbook or a JSON file
on your device; an Excel template, one risk per row with dropdowns and rating formulas, can be filled in
in Excel and opened again (docs/RISK_REGISTER.md; a blank one is in docs/risk-register-template.xlsx). Step 02 proposes risks as cause, event and effect, approves or rejects them,
and searches and filters the register. Step 03 assesses approved risks at three points (inherent,
current, target) on a heat map that draws each risk's movement, with responses, owners, costs and
actions. Promote turns the register's approved Red and Amber risks into risks in the model: the
probability band's midpoint, and a range of working days from the schedule band on the planned
duration, mapped to the activities they would delay (also `sra promote`, see docs/RISK_REGISTER.md).
The model takes its discrete risks from the register: every run promotes it first, and a model
whose risks were typed into it can move them to the register without changing its results.
Step 07 exports the reports, which now carry the risk register (heat maps now and after the
responses, the approved risks, the actions with overdue ones first) and the cost-benefit; the CSVs
add `register.csv`. Not yet in the register: P6's own risk register (XER `PROJRISK`) import, cost
impacts in the simulation (the model is schedule-only), and who approved a risk and when.

"What the results mean" turns each Summary figure into a sentence or two anyone can read: how likely
the current finish is, what the P50 and P80 promise and the contingency they need, what the mean,
median, skewness and kurtosis say about the finish, what mitigation gains, what drives the finish,
and how far to trust the run. The notes come from fixed templates and thresholds in the core library
(no AI model and no network call), so every report and the Results page say the same thing; the
rules are in [docs/READING_RESULTS.md](docs/READING_RESULTS.md). Every report carries them right
after its Summary, with the glossary.

The PDF (A4), Word (A4) and PowerPoint (16:9) reports are written in the browser, like everything
else: the same summary, notes, charts and tables as the Results, in the app's look (ink, the red accent,
2px rules, small-capital table heads, the red mark and name on every page or slide) with Archivo
embedded. Charts are pictures the browser draws from the app's own SVG charts; tables stay real,
editable tables in Word and PowerPoint. Not yet: the Gantt chart in the reports, and these three
formats from the command line (it writes the HTML report and CSVs).

The look follows the Modernist design system from `design/redesign/` (vendored as
`wwwroot/css/modernist.css`), with the Archivo font self-hosted under the SIL Open Font License,
so the app still loads nothing from other sites. The exported reports embed static Archivo instances
with Latin Extended (`wwwroot/fonts/archivo-doc-*.ttf`, made by `tools/make_doc_fonts.py`), which the
app fetches from its own site only when a report is exported. The landing page's industrial line drawings (a plant
in elevation with its schedule under the ground line) are inline SVG coloured by the design system's
tokens, and `WebAssetsTests` fails if any page, style or script in the web app refers to another site.

The Gantt chart follows P6's Activities view: the activity table beside bars on a two-tier timescale,
grouped by WBS in P6's order (`PROJWBS.seq_num`), with P6's bar colours (actual blue, remaining green,
critical red), milestones, float lines and the data date. Expand all and Collapse all (toolbar, or
right-click the chart) open or close every WBS band. Only the rows in view are rendered, so a
5,000-activity schedule stays quick. Not yet in the Gantt: relationship lines, baselines, printing.

The first load downloads the .NET runtime (~10-15 MB, cached afterwards). Simulations run on
one CPU core in the browser, so they are much slower than the command line (speed not yet
measured). For large schedules, turn on ahead-of-time compilation: run
`dotnet workload install wasm-tools` once and add `<RunAOTCompilation>true</RunAOTCompilation>`
to `ScheduleRisk.Web.csproj` before publishing (bigger download, several times faster).
For GitHub Pages the workflow sets `<base href>` to the repository's sub-folder automatically
(Cloudflare Pages serves the site at the root of its address, so it keeps `/`).
To host the site in a sub-folder anywhere else, change `<base href="/" />` in
`src/ScheduleRisk.Web/wwwroot/index.html` to that folder.

## Command line

```
sra info      project.xer
sra validate  project.xer --json health.json   # P6 Check Schedule + DCMA 14-Point (docs/SCHEDULE_CHECK.md)
sra verify    project.xer                      # our CPM dates vs the dates P6 saved in the file
sra cpm       project.xer --csv dates.csv
sra promote   project.xer --register register.json --risk model.json --out model2.json   # register risks into the model
sra register  register.xlsx --out register.json      # Excel <-> JSON; `sra register template --out r.xlsx` for a blank
sra simulate  project.xer --risk model.json --out results   # pre + post mitigation, HTML report
                                                             # add --register register.json to promote first,
                                                             # --cost-benefit for each response's days saved
```

From source: `dotnet run --project src\ScheduleRisk.Cli -c Release -- <command> ...`

`simulate` writes `report.html` (self-contained, open in any browser), `summary.pre.json`,
`summary.post.json`, `activities.csv`, `risks.csv` and `iterations.csv`. The summary JSON has a
`duration` block (working days from the project start) and a `model` block; the run's start time and
run time are left out of it, so the same seed always gives the same file.

The risk model format is described in [docs/RISK_MODEL.md](docs/RISK_MODEL.md), and how the
results are put into words in [docs/READING_RESULTS.md](docs/READING_RESULTS.md).

## The P6 check (the most important test)

P6 writes its own early/late dates and total float into every XER it exports. `sra verify`
recalculates the schedule and compares every date, in working time, with what P6 saved. A schedule
that verifies cleanly is one whose risk results you can trust. Run it on your real XER files
first; any differences point at a P6 rule the engine does not yet follow. A file without
P6-calculated dates (not scheduled before export) reports "nothing to compare" and exits 0;
only differences give a non-zero exit code.

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
their P6 dates; the project Must Finish By (`plan_end_date`; P6's calculated scheduled finish,
`scd_end_date`, is not treated as a constraint); finish, start or smallest float.

**Not yet:** ALAP constraints, expected finish dates, resource levelling, "make open-ended activities
critical", P6 XML / MS Project import. Only ALAP is noted by `validate` (as a constraint the engine
does not model); a file using the others is not detected yet.

**Risk:** three-point uncertainty (triangle, Beta-PERT, uniform) in percent or days; risk register
with probability, impact and mitigated values; risk drivers; correlation groups; Latin Hypercube
sampling; seed-reproducible results independent of CPU count; batch convergence.

**Outputs:** P5-P95 finish dates, probability of meeting the deterministic date and the project's Must
Finish By (when it has one), milestone P-dates, criticality index, duration sensitivity (Spearman),
cruciality, risk and driver tornado data, pre/post-mitigation comparison. Duration statistics: the
project duration in working days of the project calendar from the project start (the earliest start,
actual starts included), deterministic and at P5-P95, contingency (P50 and P80 minus deterministic, in
days and percent), minimum, maximum, mean, median, standard deviation, skewness and excess kurtosis
(as Excel's SKEW and KURT), with the run's start time, run time, iterations, seed, project, data date
and activity and risk counts. The Results summary (app and HTML report) adds the median finish date, the
top risk drivers, and the critical (criticality index 50% or more) and near-critical (10% to 49%)
activities. A project Must Finish By sets P6's float in the deterministic
schedule but does not change the simulation: each iteration measures criticality to its own finish,
and the risk model's `critical` filter picks the activities that drive the deterministic finish.

## Numbers from the reference engine (synthetic 500-activity schedule, 500 iterations)

Deterministic finish 13-Sep-2028; P50 15-Mar-2029, P80 29-Jun-2029 before mitigation;
P80 08-Mar-2029 after mitigation. The C# tests assert the same results.
